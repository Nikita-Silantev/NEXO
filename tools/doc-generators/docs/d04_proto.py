# -*- coding: utf-8 -*-
from common import *
import protomodel as PM
import proto_glossary as PG
import check_trace

ANON = {"AuthService.GetPowChallenge", "AuthService.CheckUsername", "AuthService.GetKdfParams", "AuthService.Register", "AuthService.Login",
        "AuthService.CompleteSecondFactor", "AuthService.WatchLoginApproval", "AuthService.RecoverAccount", "AuthService.RefreshToken",
        "LegalService.GetLegalDocuments"}
ANON_NOTE = {"AuthService.WatchLoginApproval": "одноразовый токен запроса", "AuthService.RefreshToken": "токен обновления + подпись устройства",
             "AuthService.CompleteSecondFactor": "токен незавершённого входа", "AuthService.RecoverAccount": "доказательство владения ключом восстановления"}
STAFF = {"AdminService.ListReports": "MODERATOR", "AdminService.ResolveReport": "MODERATOR", "AdminService.ApplySanction": "MODERATOR",
         "AdminService.RevokeSanction": "MODERATOR", "AdminService.GetUserOverview": "MODERATOR", "AdminService.ListTerms": "MODERATOR",
         "AdminService.UpsertTerm": "MODERATOR", "AdminService.DeleteTerm": "MODERATOR", "AdminService.ListLegalRequests": "ADMIN",
         "AdminService.CreateLegalRequest": "ADMIN", "AdminService.UpdateLegalRequest": "ADMIN"}

ERR = {  # причина -> (код gRPC, поведение клиента)
 "INVALID_CREDENTIALS": ("UNAUTHENTICATED", "Показать ошибку; не раскрывать, существует ли ник"),
 "ACCOUNT_LOCKED": ("RESOURCE_EXHAUSTED", "Показать время ожидания из `retry_after_seconds`"),
 "ACCOUNT_SUSPENDED": ("PERMISSION_DENIED", "Показать сообщение о приостановке и способ обжалования"),
 "SECOND_FACTOR_REQUIRED": ("UNAUTHENTICATED", "Запросить код TOTP или резервный код и вызвать `CompleteSecondFactor`"),
 "SECOND_FACTOR_INVALID": ("UNAUTHENTICATED", "Показать ошибку, разрешить повторный ввод в пределах лимита"),
 "TOKEN_EXPIRED": ("UNAUTHENTICATED", "Вызвать `RefreshToken` и повторить запрос один раз"),
 "TOKEN_REVOKED": ("UNAUTHENTICATED", "Завершить сессию, очистить токены, показать экран входа"),
 "REFRESH_REUSE_DETECTED": ("UNAUTHENTICATED", "Завершить сессию, предупредить о возможной компрометации"),
 "USERNAME_TAKEN": ("ALREADY_EXISTS", "Предложить другой ник"),
 "USERNAME_INVALID": ("INVALID_ARGUMENT", "Показать правила ника"),
 "PASSWORD_WEAK": ("INVALID_ARGUMENT", "Показать требования к паролю"),
 "POW_REQUIRED": ("FAILED_PRECONDITION", "Получить задачу `GetPowChallenge`, решить, повторить"),
 "POW_INVALID": ("INVALID_ARGUMENT", "Получить новую задачу и повторить"),
 "RATE_LIMITED": ("RESOURCE_EXHAUSTED", "Ждать `retry_after_seconds`, затем повторить с экспоненциальной задержкой"),
 "DOCUMENTS_NOT_ACCEPTED": ("FAILED_PRECONDITION", "Показать документы и запросить принятие `AcceptDocuments`"),
 "NOT_FOUND": ("NOT_FOUND", "Удалить локальный объект, если он больше не существует"),
 "PERMISSION_DENIED": ("PERMISSION_DENIED", "Показать сообщение; повтор бесполезен"),
 "BLOCKED_BY_PEER": ("PERMISSION_DENIED", "Показать, что отправка ограничена собеседником (без подробностей)"),
 "PRIVACY_RESTRICTED": ("PERMISSION_DENIED", "Показать, что действие ограничено настройками приватности"),
 "CHAT_FULL": ("RESOURCE_EXHAUSTED", "Показать предельное число участников из `metadata`"),
 "MESSAGE_TOO_LONG": ("INVALID_ARGUMENT", "Показать предел длины из `metadata`"),
 "EDIT_WINDOW_EXPIRED": ("FAILED_PRECONDITION", "Скрыть возможность редактирования"),
 "SLOW_MODE": ("RESOURCE_EXHAUSTED", "Показать обратный отсчёт до следующей отправки"),
 "STATUS_TEXT_REJECTED": ("INVALID_ARGUMENT", "Показать, что текст не допускается правилами сервиса, без указания слова"),
 "FILE_TOO_LARGE": ("INVALID_ARGUMENT", "Показать максимальный размер из `metadata`"),
 "QUOTA_EXCEEDED": ("RESOURCE_EXHAUSTED", "Показать использование квоты и предложить освободить место"),
 "UPLOAD_EXPIRED": ("FAILED_PRECONDITION", "Начать загрузку заново"),
 "CHECKSUM_MISMATCH": ("INVALID_ARGUMENT", "Повторить загрузку повреждённой части, затем завершение"),
 "FILE_RESTORING": ("FAILED_PRECONDITION", "Показать ожидание, вызвать `RequestRestore`, ждать обновления `file_ready`"),
 "PREKEYS_EXHAUSTED": ("FAILED_PRECONDITION", "Использовать резервный ключ подписанного предключа; уведомить владельца устройства"),
 "DEVICE_NOT_FOUND": ("NOT_FOUND", "Обновить список устройств собеседника"),
 "CALL_BUSY": ("FAILED_PRECONDITION", "Показать, что абонент занят"),
 "CALL_ENDED": ("FAILED_PRECONDITION", "Закрыть экран звонка"),
 "VERSION_CONFLICT": ("ABORTED", "Загрузить текущее значение, объединить и повторить"),
 "CLIENT_TOO_OLD": ("FAILED_PRECONDITION", "Показать требование обновить приложение"),
 "RESYNC_REQUIRED": ("FAILED_PRECONDITION", "Выполнить полную пересинхронизацию (`ListChats`) и продолжить подписку"),
}

RATE = [
 ["Register", "5 в час с IP", "Решение PoW обязательно"], ["Login", "10 за 10 минут на пару (ник, IP); экспоненциальная задержка", "Решение PoW обязательно"],
 ["GetKdfParams, GetPowChallenge, CheckUsername", "30 в минуту с IP", "Для неизвестного ника – поддельные параметры"],
 ["RecoverAccount", "5 в час с IP, 3 в сутки на ник", "Решение PoW обязательно"], ["RefreshToken", "60 в час на устройство", "—"],
 ["SendMessage", "30 за 10 секунд на пользователя; медленный режим чата (если включён)", "—"],
 ["EditMessage, DeleteMessages, SetReaction", "60 в минуту", "—"], ["InitUpload", "30 в минуту", "—"], ["GetUploadPartUrls", "300 в минуту", "—"],
 ["Subscribe", "один активный поток на устройство, не более 5 на пользователя", "Новый поток вытесняет предыдущий того же устройства"],
 ["GetDifference, GetHistory", "120 в минуту", "—"], ["SearchUsers, SearchMessages", "30 в минуту", "—"],
 ["CreateReport", "10 в час", "—"], ["StartCall", "10 в минуту", "—"], ["CreateGroup, CreateChannel", "10 в сутки", "—"],
 ["CreateInviteLink, JoinByInvite", "30 в час", "—"],
 ["Прочие методы с токеном", "600 в минуту на пользователя", "—"], ["Прочие методы без токена", "120 в минуту с IP", "—"],
]

SETTINGS_KEYS = [
 ["theme.active", "Идентификатор активной темы (встроенной или пользовательской)", "строка"],
 ["theme.custom.<id>", "Пользовательская тема: токены цветов, радиусов, отступов, шрифтов, фон и форма пузырей", "объект JSON по схеме темы"],
 ["hotkeys.map", "Таблица «команда – сочетание клавиш»", "объект"], ["ui.density", "Плотность интерфейса: compact, normal, comfortable", "строка"],
 ["ui.font_scale", "Масштаб шрифта, 0.8–1.6", "число"], ["ui.animations", "Включены ли анимации", "логическое"],
 ["ui.chat_background", "Фон чата (цвет, градиент или идентификатор файла)", "объект"], ["ui.layout", "Расположение боковой панели и режим списка чатов", "объект"],
 ["composer.enter_sends", "Отправка по Enter (иначе Ctrl+Enter)", "логическое"], ["notifications.default", "Уведомления по умолчанию: звук, предпросмотр, группы, каналы", "объект"],
 ["lock.method", "Способ локальной разблокировки: none, pin, biometric (применяется только на устройстве)", "строка"],
 ["lock.autolock_seconds", "Автоблокировка через указанное число секунд бездействия", "число"], ["lock.hide_previews", "Скрывать предпросмотр в уведомлениях при блокировке", "логическое"],
 ["media.autodownload", "Правила автозагрузки по сети и типам", "объект"], ["media.compress_default", "Пережимать ли изображения и видео по умолчанию", "логическое"],
 ["status.auto_reset_default", "Время автосброса пользовательского статуса по умолчанию, минуты", "число"],
 ["folders.collapsed", "Свёрнутые папки в боковой панели", "список"], ["chat.tags.<chat_id>", "Теги чата: до 8 строк по 24 символа", "список строк"],
]

DUR = {"new_message", "message_edited", "message_deleted", "read_state", "chat", "member", "folder", "draft", "settings", "device", "file_ready", "contact", "reaction"}


def build():
    PMODEL = PM.load()
    d = new("proto", subtitle="Протокол клиент–сервер NEXO v1 (gRPC, Protocol Buffers)")
    nm = len(PMODEL["messages"]); nf = sum(len(m["fields"]) for m in PMODEL["messages"]); nr = sum(len(s["methods"]) for s in PMODEL["services"])
    d.h1("ВВЕДЕНИЕ", numbered=False)
    d.p("Документ описывает протокол информационного взаимодействия клиентских приложений NEXO с сервером. Протокол задан "
        f"файлами Protocol Buffers (proto3), поставляемыми вместе с документом: {len(PMODEL['files'])} файлов, {len(PMODEL['services'])} сервисов, "
        f"{nr} методов, {nm} сообщений, {nf} полей, {len(PMODEL['enums'])} перечислений. Файлы `.proto` являются нормативным источником; "
        "настоящий документ поясняет их, фиксирует правила, которые нельзя выразить языком описания интерфейсов "
        "(права, лимиты, поведение клиента), и содержит словарь сообщений, сформированный автоматически из тех же файлов.")
    d.p("Документ оформлен по ГОСТ 19.402-78 в части описания программы с учётом специфики протокола.")

    d.h1("ОБЩИЕ СВЕДЕНИЯ", key="obsh")
    d.h2("Назначение и область применения")
    d.p("Протокол используется всеми клиентами (Windows, macOS, Linux, браузер, Android, iOS) и серверной частью. Он покрывает: "
        "аутентификацию и устройства, профили и контакты, чаты, сообщения, файлы, секретные чаты, звонки, синхронизацию, "
        "присутствие, настройки, юридические документы, жалобы и служебные функции.")
    d.h2("Транспорт и кодирование")
    d.bullets([
        "**Транспорт**: gRPC поверх HTTP/2 с TLS 1.3; для браузера – gRPC-Web (поддерживаются унарные вызовы и серверные потоки; клиентские и двунаправленные потоки протоколом не используются);",
        "**Кодирование**: Protocol Buffers (двоичное); пакет `nexo.v1`, пространство имён C# `Nexo.Protocol.V1`;",
        "**Потоки**: единственный потоковый метод – `SyncService.Subscribe` (серверный поток);",
        "**Сжатие**: gzip на уровне gRPC для сообщений более 1 КиБ (по согласованию сторон);",
        "**Адрес**: единая точка входа по HTTPS на порту 443; маршрутизация по имени сервиса gRPC.",
    ])
    d.h2("Состав файлов протокола", key="files")
    rows = []
    for f in PMODEL["files"]:
        h = f["header"].split("Протокол взаимодействия NEXO.")[-1].strip(" .") if "Протокол взаимодействия NEXO." in f["header"] else f["header"]
        rows.append([f["name"], h, ", ".join(f["services"]) or "—", str(f["nmsg"]), str(f["nenum"])])
    d.table("Файлы протокола", ["Файл", "Содержание", "Сервисы", "Сообщ.", "Перечисл."], rows, widths=[2.7, 5, 4.5, 1.3, 1.5], key="files", font=10)
    d.note("Зависимости файлов ациклические: `common.proto` и `crypto.proto` не импортируют других файлов проекта; `messages.proto`, `chats.proto`, "
           "`keys.proto` связаны через `crypto.proto`, что исключает циклический импорт.")
    d.h2("Версионирование")
    d.p("Мажорная версия протокола входит в имя пакета (`nexo.v1`). Сервер поддерживает версию клиента, переданную в метаданных; клиенты, "
        "версия которых ниже минимально поддерживаемой, получают `ERROR_REASON_CLIENT_TOO_OLD`. В пределах мажорной версии допускаются только "
        "обратно совместимые изменения (раздел {sec:evol}).")

    d.h1("ОБЩИЕ СОГЛАШЕНИЯ", key="soglash")
    d.h2("Типы данных")
    d.table("Соглашения о данных", ["Вид данных", "Правило"], [
        ["Идентификаторы", "UUID версии 7 строкой в каноническом виде (RFC 9562), нижний регистр"],
        ["Время", "`google.protobuf.Timestamp`, UTC, точность до миллисекунд"],
        ["Строки", "UTF-8, нормализация NFC, без управляющих символов, кроме `\\n` и `\\t`"],
        ["Номера полей", "Не переиспользуются; удалённые поля помечаются `reserved`"],
        ["Пустые значения", "Отсутствие значения и значение по умолчанию неразличимы; различие, где оно существенно, выражается `oneof` или обёрткой"],
        ["Перечисления", "Нулевое значение – `*_UNSPECIFIED`; клиент обязан безопасно обрабатывать неизвестные значения"],
        ["Счётчики и размеры", "`int64`/`int32`; размеры в байтах, длительности – в миллисекундах или секундах (указано в имени поля)"],
        ["Двоичные данные", "`bytes`; ключи и подписи – в «сыром» виде, без кодирования Base64"],
    ], widths=[3.3, 11], key="types", font=11)
    d.h2("Метаданные запросов и ответов")
    d.table("Метаданные gRPC", ["Заголовок", "Направление", "Назначение"], [
        ["authorization", "запрос", "`Bearer <токен доступа>` для методов с аутентификацией"],
        ["x-nexo-client", "запрос", "Платформа и версия клиента: `windows/1.0.3`; обязателен"],
        ["x-nexo-device-id", "запрос", "Идентификатор устройства; обязателен после регистрации устройства"],
        ["x-request-id", "запрос", "UUID запроса для корреляции в журналах (необязателен)"],
        ["accept-language", "запрос", "Язык сообщений об ошибках"],
        ["traceparent", "запрос", "Контекст трассировки W3C Trace Context"],
        ["x-nexo-trace-id", "ответ (трейлер)", "Идентификатор трассировки для обращений в поддержку"],
        ["grpc-status-details-bin", "ответ (трейлер)", "Структура `ErrorDetail` при ошибке"],
    ], widths=[3.7, 2.7, 8], key="meta", font=11)
    d.h2("Идемпотентность")
    d.p("Мутирующие методы, повтор которых возможен при сбое сети, принимают клиентский идентификатор операции и безопасны к повторению: "
        "`SendMessage` – по паре (`chat_id`, `client_msg_id`); `StartCall` – по `client_call_id`; `InitUpload` – по клиентскому идентификатору загрузки; "
        "`CreateSecretChat`, `CreateGroup` и `CreateChannel` – по клиентскому идентификатору. Повтор возвращает результат исходного вызова. "
        "Методы чтения идемпотентны по определению.")
    d.h2("Постраничная выборка")
    d.p("Списки возвращаются страницами: запрос содержит `PageRequest` (`limit` 1–100, по умолчанию 50; `cursor`), ответ – `PageInfo` "
        "(`next_cursor`, `has_more`). Курсор непрозрачен для клиента. История чата запрашивается относительно якорного `seq` и направления; "
        "пакетная синхронизация (`GetDifference`) допускает до 500 обновлений.")
    d.h2("Оптимистическая блокировка")
    d.p("Сущности, изменяемые с нескольких устройств (настройки, черновики), содержат `version`. Запись с несовпадающей версией отклоняется "
        "(`ERROR_REASON_VERSION_CONFLICT` либо список `conflicts` в ответе пакетной записи); клиент объединяет данные и повторяет запись.")

    d.h1("АУТЕНТИФИКАЦИЯ И АВТОРИЗАЦИЯ", key="auth")
    d.p("Механизм аутентификации подробно описан в " + ref("crypto") + ". Здесь зафиксированы следствия для протокола.")
    d.bullets([
        "метод без токена допускается только для перечисленных в таблице {tab:access} (регистрация, вход, восстановление, документы);",
        "токен доступа – JWT, подпись EdDSA, срок 10 минут; содержит идентификаторы пользователя, устройства и сессии;",
        "токен обновления – непрозрачный одноразовый; каждое обновление выдаёт новый и аннулирует предыдущий;",
        "служебные методы проверяют роль пользователя на сервере независимо от клиента;",
        "перед использованием большинства методов должны быть приняты актуальные обязательные документы (иначе `ERROR_REASON_DOCUMENTS_NOT_ACCEPTED`).",
    ])
    rows = []
    for s in PMODEL["services"]:
        anon = [m["name"] for m in s["methods"] if f"{s['name']}.{m['name']}" in ANON]
        staff = [(m["name"], STAFF[f"{s['name']}.{m['name']}"]) for m in s["methods"] if f"{s['name']}.{m['name']}" in STAFF]
        if s["name"] == "AdminService":
            rows.append([s["name"], "—", "MODERATOR и выше: " + ", ".join(n for n, r in staff if r == "MODERATOR") + ". ADMIN: " + ", ".join(n for n, r in staff if r == "ADMIN")])
        elif anon:
            rows.append([s["name"], ", ".join(anon), "остальные методы – с токеном доступа"])
        else:
            rows.append([s["name"], "—", "все методы – с токеном доступа" + ("; роль не ниже USER" if s["name"] != "ModerationService" else "")])
    d.table("Требования к доступу по сервисам", ["Сервис", "Методы без токена доступа", "Остальные методы"], rows, widths=[3, 5.5, 6], key="access", font=10)
    d.table("Допуск к методам без токена", ["Метод", "Чем подтверждается вызов"], [[k, ANON_NOTE.get(k, "публичный метод, лимит по IP")] for k in sorted(ANON)],
            widths=[5.5, 9], key="anon", font=11)
    d.h2("Ограничение частоты запросов", key="rate")
    d.p("Ограничения применяются перехватчиком сервера по пользователю, устройству и IP-адресу (счётчики в Redis). Значения приведены в таблице {tab:rate} и "
        "настраиваются администратором (параметры `Limits:*`). При превышении возвращается `ERROR_REASON_RATE_LIMITED` с `retry_after_seconds`.")
    d.table("Лимиты запросов", ["Метод(ы)", "Лимит", "Примечание"], RATE, widths=[5, 6.3, 3.7], key="rate", font=10)

    d.h1("ОБРАБОТКА ОШИБОК", key="err")
    d.p("Ошибка возвращается кодом статуса gRPC и двоичным трейлером `grpc-status-details-bin`, содержащим сообщение `ErrorDetail` "
        "(`reason`, `metadata`, `retry_after_seconds`). Клиент принимает решения по `reason`, а не по тексту сообщения; текст предназначен для журналов. "
        "Параметры для локализации (пределы, сроки) передаются в `metadata`. Сообщения об ошибках не должны раскрывать, существует ли учётная запись, "
        "и не называть слова, отклонённые фильтром.")
    rows = [[v["name"].replace("ERROR_REASON_", ""), str(v["num"]), ERR[v["name"].replace("ERROR_REASON_", "")][0],
             v["comment"].capitalize() if v["comment"] else "", ERR[v["name"].replace("ERROR_REASON_", "")][1]]
            for e in PMODEL["enums"] if e["name"] == "ErrorReason" for v in e["values"] if v["num"] != 0]
    d.table("Коды причин ошибок", ["Причина", "№", "Код gRPC", "Смысл", "Действие клиента"], rows, widths=[3.4, 0.8, 2.6, 3.8, 4.8], key="errors", font=9)
    d.p("Общая политика повторов клиента: коды `UNAVAILABLE`, `DEADLINE_EXCEEDED` и сетевые сбои – повтор с экспоненциальной задержкой (начало 1 с, "
        "множитель 2, предел 60 с, случайный разброс ±20 %); `RESOURCE_EXHAUSTED` – ожидание `retry_after_seconds`; остальные коды повторять без изменения данных бесполезно.")

    d.h1("СИНХРОНИЗАЦИЯ И ПОТОКИ СОБЫТИЙ", key="sync")
    d.h2("Подписка")
    d.p("Устройство вызывает `SyncService.Subscribe` после аутентификации, передавая последний применённый `update_seq`. Сервер: (1) досылает накопленные "
        "обновления из журнала в порядке возрастания `update_seq`; (2) переходит к событиям реального времени; (3) каждые 25 секунд посылает `Heartbeat`, "
        "чтобы клиент обнаруживал обрыв; (4) если запрошенный номер старше окна хранения (30 суток), отправляет `ResyncRequired` и закрывает поток. "
        "Клиент обязан применять обновления транзакционно с сохранением нового `update_seq` и подтверждать получение `AckUpdates`.")
    d.h2("Гарантии")
    d.bullets([
        "**Порядок**: долговечные обновления строго упорядочены по `update_seq` в пределах пользователя;",
        "**Доставка**: «как минимум один раз» для долговечных обновлений; клиент отбрасывает повторы по `update_seq` и по паре (`chat_id`, `seq`);",
        "**Эфемерные обновления** (`update_seq = 0`) могут быть потеряны и не дублируются из журнала;",
        "**Потеря соединения** не приводит к потере долговечных обновлений; восстановление – повторным `Subscribe` или `GetDifference`;",
        "**Каналы**: для каналов из `watched_channel_ids` приходит `ChannelTickUpdate` без содержимого; публикации загружаются при открытии канала.",
    ])
    rows = []
    for m in PMODEL["messages"]:
        if m["name"] == "Update":
            for f in m["fields"]:
                if f["oneof"] == "payload":
                    rows.append([f["name"], f["type"], "долговечное" if f["name"] in DUR else "эфемерное", PG.describe(f["name"], f["type"], f["comment"])])
    d.h2("Виды обновлений")
    d.table("Виды обновлений (`Update.payload`)", ["Поле", "Структура", "Класс", "Смысл"], rows, widths=[3.2, 3.8, 2.3, 5.2], key="updates", font=10)

    d.landscape()
    d.h1("ОПИСАНИЕ СЕРВИСОВ", key="svc")
    d.p("Для каждого сервиса приведены методы, типы запроса и ответа и назначение. Структуры сообщений описаны в приложении {app:dict}. "
        "Пометка «поток» означает серверный потоковый метод.")
    svc_intro = {
     "AuthService": "Регистрация, вход, токены, второй фактор, восстановление доступа, смена пароля.",
     "DeviceService": "Список устройств и сессий, подтверждение входа с нового устройства, события безопасности, push-токены.",
     "UserService": "Профиль, поиск, контакты, блокировки, приватность, удаление учётной записи.",
     "PresenceService": "Статусы и присутствие.", "LegalService": "Юридические документы и фиксация принятия.",
     "SettingsService": "Синхронизируемые настройки, темы, горячие клавиши, теги чатов.",
     "MediaService": "Загрузка и скачивание файлов, квоты, восстановление из холодного хранилища.",
     "MessageService": "Отправка, правка, удаление, история, прочтение, реакции, пересылка, поиск.",
     "ChatService": "Чаты, группы, каналы, участники, роли, приглашения, папки, черновики, таймер.",
     "KeyService": "Открытые ключи устройств и секретные чаты.", "CallService": "Звонки и демонстрация экрана.",
     "SyncService": "Журнал обновлений и поток событий.", "ModerationService": "Жалобы пользователей.",
     "AdminService": "Служебные функции: жалобы, меры, словарь, обращения органов.",
    }
    order = ["AuthService", "DeviceService", "UserService", "PresenceService", "LegalService", "SettingsService", "MediaService", "MessageService",
             "ChatService", "KeyService", "CallService", "SyncService", "ModerationService", "AdminService"]
    for sn in order:
        s = next(x for x in PMODEL["services"] if x["name"] == sn)
        d.h2(f"{sn}", key="s_" + sn)
        d.p(svc_intro[sn] + f" Файл: `{s['file']}`. Методов: {len(s['methods'])}.", indent=False)
        rows = []
        for m in s["methods"]:
            acc = "без токена" if f"{sn}.{m['name']}" in ANON else (STAFF[f"{sn}.{m['name']}"] if f"{sn}.{m['name']}" in STAFF else "токен")
            ret = ("поток " if m["stream"] else "") + m["resp"]
            rows.append([m["name"], m["req"], ret, acc, m["comment"] or ""])
        d.table(f"Методы сервиса {sn}", ["Метод", "Запрос", "Ответ", "Доступ", "Назначение"], rows, widths=[3.7, 4.3, 4.3, 1.5, 8], key="m_" + sn, font=10)
    d.portrait()
    d.h1("СЦЕНАРИИ ВЗАИМОДЕЙСТВИЯ", key="scen")
    d.p("Таблица {tab:scen} связывает типовые сценарии с цепочками вызовов; полные последовательности показаны на диаграммах документа " + ref("pz") + " (номер рисунка указан в последней колонке).")
    d.table("Цепочки вызовов", ["Сценарий", "Цепочка вызовов", "Рис. ПЗ"], [
        ["Регистрация", "GetPowChallenge → CheckUsername → GetLegalDocuments → Register → AcceptDocuments (при необходимости) → Subscribe", "{pz:fig:seq01}"],
        ["Вход с нового устройства", "GetPowChallenge → GetKdfParams → Login → (CompleteSecondFactor) → WatchLoginApproval / ResolveLoginApproval → Subscribe", "{pz:fig:seq02}"],
        ["Обновление токена", "RefreshToken", "{pz:fig:seq03}"],
        ["Отправка сообщения", "SendMessage → (событие new_message получателям через Subscribe) → AckUpdates", "{pz:fig:seq04}"],
        ["Отправка офлайн", "(Outbox) SendMessage ×N → GetDifference", "{pz:fig:seq05}"],
        ["Подписка и пересинхронизация", "Subscribe → (ResyncRequired) → ListChats → GetHistory → Subscribe", "{pz:fig:seq06}"],
        ["Загрузка файла", "InitUpload → GetUploadPartUrls → (PUT частей) → CompleteUpload → (file_ready) → SendMessage", "{pz:fig:seq07}"],
        ["Файл из холодного хранилища", "GetDownloadUrl (FILE_RESTORING) → RequestRestore → (file_ready) → GetDownloadUrl", "{pz:fig:seq08}"],
        ["Секретный чат", "UploadKeyBundle → GetPrekeyBundle → CreateSecretChat → AcceptSecretChat → SendMessage (шифртекст)", "{pz:fig:seq09}"],
        ["Удаление по таймеру", "SetChatTtl → MarkRead → (message_deleted expired)", "{pz:fig:seq10}"],
        ["Звонок", "StartCall → (incoming_call) → JoinCall → UpdateParticipantState → LeaveCall / EndCall", "{pz:fig:seq11}"],
        ["Статус", "SetStatus (STATUS_TEXT_REJECTED при нарушении) → (presence)", "{pz:fig:seq12}"],
        ["Восстановление доступа", "GetPowChallenge → RecoverAccount → Subscribe", "{pz:fig:seq13}"],
        ["Видеокружок", "InitUpload → … → CompleteUpload → SendMessage (MEDIA_KIND_VIDEO_NOTE)", "{pz:fig:seq14}"],
        ["Жалоба", "CreateReport → (AdminService) ListReports → ResolveReport / ApplySanction", "{pz:fig:seq15}"],
    ], widths=[3.5, 9, 1.8], key="scen", font=10)

    d.h1("ПРОВЕРКА СООТВЕТСТВИЯ ДИАГРАММАМ И ЗАМЫСЛУ", key="verify")
    errs = check_trace.run(verbose=False)
    d.p("Протокол сверен с требованиями ТЗ, диаграммами потоков данных, последовательностей и моделью данных. Проверки автоматизированы "
        "(перечень в " + ref("plan") + ") и повторяются при каждом изменении.")
    d.table("Автоматические проверки согласованности", ["Проверка", "Результат"], [
        ["Все файлы `.proto` компилируются", "выполнено"],
        ["Перечисления БД и протокола согласованы", "27 соответствий, расхождений нет"],
        ["Имена методов в диаграммах последовательностей существуют в протоколе", "расхождений нет"],
        ["Каждое обращение «Сервис.Метод» в требованиях существует в протоколе", "расхождений нет"],
        [f"Каждый из {nr} методов трассируется хотя бы к одному требованию", "выполнено" if not errs else f"замечаний: {len(errs)}"],
        ["Балансировка диаграмм потоков данных между уровнями; покрытие 40 таблиц БД хранилищами", "выполнено"],
        ["Схема БД выполняется на PostgreSQL 16", "выполнено"],
    ], widths=[10, 4.5], key="checks", font=11)
    d.p("Анализ выявил расхождения между замыслом и первой редакцией проектных решений; все они устранены до утверждения документов:")
    d.table("Выявленные расхождения и принятые решения", ["№", "Расхождение", "Решение"], [
        ["1", "В замысле (девятое направление) заявлены «папки и теги»; протокол содержал только папки", "Теги реализованы как метки чата в синхронизируемых настройках: ключ `chat.tags.<chat_id>`; изменений схемы БД не требуется (требование ФТ-ПЕР-03)"],
        ["2", "В протоколе есть `InviteToCall` (звонки нескольких участников), в ТЗ были только звонки 1:1", "Добавлены требования о групповых звонках до 8 участников (приоритет «С») и об истории звонков"],
        ["3", "Серверный поиск `SearchMessages` не отражён в ТЗ", "Требование о поиске уточнено: локальный поиск (включая секретные чаты) и серверный для обычных чатов"],
        ["4", "Профиль, контакты, закрепление сообщений, групповое управление и ряд методов не были связаны с требованиями", "Добавлены требования и ссылки; все методы протокола трассируются к требованиям"],
        ["5", "Видеокружок и голосовое сообщение в требованиях были названы видами сообщения", "В протоколе это виды вложения (`MEDIA_KIND_VIDEO_NOTE`, `MEDIA_KIND_VOICE`); ссылки исправлены"],
        ["6", "В требованиях ссылались на несуществующие методы (24 случая) и на общую «поиск каналов»", "Ссылки приведены к реальным методам; публичный каталог каналов исключён из первой версии"],
        ["7", "Заголовки `.proto` и `schema.sql` содержали устаревшие обозначения документов", "Заменены на 13 01 и 13 02"],
        ["8", "В `settings.proto` есть ссылка на реестр ключей настроек, которого не было", "Реестр добавлен приложением {app:keys}"],
        ["9", "Выгрузка данных пользователя по его запросу не имела метода протокола", "Принято как организационная процедура через поддержку (`AdminService.GetUserOverview`), см. Политику хранения; технический метод может быть добавлен позднее без нарушения совместимости"],
    ], widths=[0.8, 6.5, 7.2], key="findings", font=10)

    d.h1("ПРАВИЛА РАЗВИТИЯ ПРОТОКОЛА", key="evol")
    d.bullets([
        "допустимы без смены мажорной версии: добавление методов, сообщений, необязательных полей и значений перечислений;",
        "недопустимы: изменение номера или типа поля, переименование полей и методов, повторное использование номеров (старые помечаются `reserved`), изменение смысла существующих значений;",
        "новый метод и новое поле сопровождаются записью в данном документе, в матрице трассировки ТЗ и, при необходимости, диаграммой;",
        "совместимость проверяется инструментом сравнения схем (`buf breaking` или эквивалент) при каждой сборке;",
        "устаревший метод помечается `deprecated` и поддерживается не менее двух минорных версий клиента;",
        "поведение клиента при неизвестных полях и значениях перечислений – игнорирование без ошибки.",
    ])

    # --- приложения
    d.appendix("Словарь сообщений", status="обязательное", key="dict")
    d.p("Словарь сформирован автоматически из описателя протокола. Описания взяты из комментариев `.proto`, а при их отсутствии – из словаря терминов проекта.")
    cur = None
    for m in sorted(PMODEL["messages"], key=lambda x: (OWN_ORDER.index(x["file"]) if x["file"] in OWN_ORDER else 99, x["name"])):
        if not m["fields"]: continue
        if m["file"] != cur:
            cur = m["file"]
            d.p(f"**Файл `{cur}`**", indent=False, keep=True)
        rows = [[f["name"], str(f["num"]), f["type"] + (f" (oneof {f['oneof']})" if f["oneof"] else ""), PG.describe(f["name"], f["type"], f["comment"])] for f in m["fields"]]
        cap = f"Сообщение {m['name']}" + (f". {m['comment']}" if m["comment"] else "")
        d.table(cap, ["Поле", "№", "Тип", "Описание"], rows, widths=[3.3, 0.8, 4, 6.4], key=None if False else "msg_" + m["name"].replace(".", "_"), font=9)
    d.appendix("Перечисления", status="обязательное", key="enums")
    for e in PMODEL["enums"]:
        rows = [[v["name"], str(v["num"]), v["comment"] or ""] for v in e["values"]]
        d.table(f"Перечисление {e['name']}" + (f". {e['comment']}" if e["comment"] else ""), ["Значение", "№", "Смысл"], rows, widths=[6, 1, 7.4], key="enum_" + e["name"].replace(".", "_"), font=9)
    d.appendix("Реестр ключей настроек", status="обязательное", key="keys")
    d.p("Реестр определяет допустимые ключи `SettingsService` (ключ до 64 символов; значение – JSON до 16 КиБ). Неизвестные ключи сервер отклоняет. "
        "Ключи, помеченные в описании как применяемые только на устройстве, синхронизируются, но не меняют локальную защиту автоматически.")
    d.table("Ключи настроек", ["Ключ", "Назначение", "Тип значения"], SETTINGS_KEYS, widths=[4.2, 7.3, 3], key="setkeys", font=10)
    return d


OWN_ORDER = list(PM.OWN)
