using System.Text;
using Nexo.Core;

// Минимальный запускатель тестов без внешних пакетов: dotnet run --project tests/Nexo.Core.Tests
int failed = 0, passed = 0;
void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("  ok   " + name); }
    catch (Exception e) { failed++; Console.WriteLine("  FAIL " + name + ": " + e.Message); }
}
void True(bool c, string m = "условие ложно") { if (!c) throw new Exception(m); }
void Eq<T>(T a, T b) { if (!EqualityComparer<T>.Default.Equals(a, b)) throw new Exception($"{a} != {b}"); }

Test("PoW: найденное решение проходит проверку", () =>
{
    var ch = Pow.NewChallenge();
    var nonce = Pow.Solve(ch, 12);
    True(Pow.Verify(ch, nonce, 12));
});
Test("PoW: чужой nonce не подходит к другой задаче", () =>
{
    var ch1 = Pow.NewChallenge(); var ch2 = Pow.NewChallenge();
    var nonce = Pow.Solve(ch1, 14);
    // вероятность случайного совпадения 2^-14 – допустимо для теста; повторяем несколько задач
    int ok = 0; for (int i = 0; i < 20; i++) { var c = Pow.NewChallenge(); if (Pow.Verify(c, nonce, 14)) ok++; }
    True(ok <= 1);
    _ = ch2;
});
Test("PoW: неверная длина challenge/nonce отклоняется", () =>
{
    True(!Pow.Verify(new byte[5], new byte[8], 1));
    True(!Pow.Verify(new byte[16], Array.Empty<byte>(), 1));
    True(!Pow.Verify(new byte[16], new byte[33], 1));
});
Test("PoW: LeadingZeroBits", () =>
{
    Eq(Pow.LeadingZeroBits(new byte[] { 0, 0, 0x10 }), 19);
    Eq(Pow.LeadingZeroBits(new byte[] { 0x80 }), 0);
    Eq(Pow.LeadingZeroBits(new byte[] { 0x01 }), 7);
    Eq(Pow.LeadingZeroBits(new byte[] { 0, 0 }), 16);
});
Test("PoW: адаптивная сложность в пределах 18..26", () =>
{
    Eq(Pow.DifficultyFor(0), 18); Eq(Pow.DifficultyFor(3), 19); Eq(Pow.DifficultyFor(10_000), 26);
});
Test("Tokens: opaque 256 бит, уникальны, хэш 32 байта", () =>
{
    var a = Tokens.NewOpaque(); var b = Tokens.NewOpaque();
    True(a != b); Eq(Base64Url.Decode(a).Length, 32); Eq(Tokens.Hash(a).Length, 32);
    True(Tokens.ConstantTimeEquals(Tokens.Hash(a), Tokens.Hash(a)));
    True(!Tokens.ConstantTimeEquals(Tokens.Hash(a), Tokens.Hash(b)));
});
Test("Base64Url: круговой обход для разных длин", () =>
{
    for (int n = 0; n < 40; n++) { var d = new byte[n]; Random.Shared.NextBytes(d); True(Base64Url.Decode(Base64Url.Encode(d)).AsSpan().SequenceEqual(d)); }
});
Test("Tokens: числовой код фиксированной длины", () =>
{
    for (int i = 0; i < 200; i++) Eq(Tokens.NewNumericCode().Length, 6);
});
Test("Ник: допустимые и недопустимые", () =>
{
    True(UsernameRules.IsValid("nikita_1"));
    True(!UsernameRules.IsValid("ab"));
    True(!UsernameRules.IsValid("Nikita"));           // нужно нормализовать до проверки
    True(UsernameRules.IsValid(UsernameRules.Normalize(" Nikita ")));
    True(!UsernameRules.IsValid("ник"));
    True(!UsernameRules.IsValid("admin"));
    True(!UsernameRules.IsValid(new string('a', 33)));
});
Test("Отображаемое имя", () =>
{
    True(UsernameRules.IsValidDisplayName("Никита"));
    True(!UsernameRules.IsValidDisplayName("  "));
    True(!UsernameRules.IsValidDisplayName("a\u0007b"));
    True(!UsernameRules.IsValidDisplayName(new string('я', 65)));
});
Test("FakeKdf: детерминирован, зависит от ника и секрета", () =>
{
    var s = Encoding.UTF8.GetBytes("secret-1"); var s2 = Encoding.UTF8.GetBytes("secret-2");
    True(FakeKdf.Salt(s, "ghost").AsSpan().SequenceEqual(FakeKdf.Salt(s, "ghost")));
    True(!FakeKdf.Salt(s, "ghost").AsSpan().SequenceEqual(FakeKdf.Salt(s, "ghost2")));
    True(!FakeKdf.Salt(s, "ghost").AsSpan().SequenceEqual(FakeKdf.Salt(s2, "ghost")));
    Eq(FakeKdf.Salt(s, "ghost").Length, 16);
});
Test("Лимитер: блокирует сверх лимита и отпускает после окна", () =>
{
    var now = DateTimeOffset.UtcNow;
    var rl = new SlidingWindowLimiter(() => now);
    var w = TimeSpan.FromMinutes(1);
    for (int i = 0; i < 3; i++) True(rl.TryAcquire("k", 3, w, out _));
    True(!rl.TryAcquire("k", 3, w, out var ra)); True(ra > TimeSpan.Zero);
    now += TimeSpan.FromSeconds(61);
    True(rl.TryAcquire("k", 3, w, out _));
    Eq(rl.Count("k", w), 1);
    rl.Reset("k"); Eq(rl.Count("k", w), 0);
});
Test("IP: маскирование IPv4 и IPv6", () =>
{
    Eq(IpMask.Mask("203.0.113.77"), "203.0.113.***");
    True(IpMask.Mask("2001:db8:85a3::8a2e:370:7334").EndsWith(":****"));
    Eq(IpMask.Mask("мусор"), "");
});
Test("Политика входа: задержка растёт и ограничена", () =>
{
    Eq(LoginPolicy.Backoff(0), TimeSpan.Zero);
    True(LoginPolicy.Backoff(3) > LoginPolicy.Backoff(1));
    True(LoginPolicy.Backoff(50) <= TimeSpan.FromSeconds(300));
});

Test("Подписи устройства: канонические сообщения однозначны", () =>
{
    var m1 = DeviceSignatures.RefreshMessage("tok", "dev", 1000);
    var m2 = DeviceSignatures.RefreshMessage("tok", "dev", 1001);
    True(!m1.AsSpan().SequenceEqual(m2));
    Eq(m1.Length, 3 + 3 + 8);
    var a = DeviceSignatures.ApprovalMessage("id", new byte[32], "123456");
    Eq(a.Length, 2 + 32 + 6);
});

Console.WriteLine($"\nПройдено: {passed}, провалено: {failed}");
return failed == 0 ? 0 : 1;
