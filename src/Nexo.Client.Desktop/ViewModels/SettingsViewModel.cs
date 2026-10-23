using System.Collections.ObjectModel;
using System.Reactive;
using Google.Protobuf.WellKnownTypes;
using Nexo.Client.Core;
using Nexo.Protocol.V1;
using ReactiveUI;

namespace Nexo.Client.Desktop.ViewModels;

public sealed class DeviceItem(DeviceSummary d)
{
    public DeviceSummary Device { get; } = d;
    public string Name => Device.Name + (Device.IsCurrent ? " (это устройство)" : "");
    public string Platform => Device.Platform switch { DevicePlatform.Windows => "Windows", DevicePlatform.Macos => "macOS", DevicePlatform.Linux => "Linux", DevicePlatform.Android => "Android", DevicePlatform.Ios => "iOS", _ => "Веб" };
    public string LastSeen => Device.LastSeenAt is null ? "—" : Device.LastSeenAt.ToDateTime().ToLocalTime().ToString("g") + (string.IsNullOrEmpty(Device.LastIpMasked) ? "" : " · " + Device.LastIpMasked);
    public bool CanRevoke => !Device.IsCurrent;
}

/// <summary>Безопасность и устройства (ФТ-УСТ-01..04, ФТ-ПРИ-01..03, НФТ-БЕЗ-05).</summary>
public sealed class SettingsViewModel : FormVm
{
    private readonly ShellViewModel _shell; private readonly AppSession _s;
    private string _pin = "", _pin2 = "", _info = "";
    private int _autoLock; private bool _wipe, _lockMin;

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    public SettingsViewModel(ShellViewModel shell, AppSession s)
    {
        _shell = shell; _s = s;
        _autoLock = shell.Settings.AutoLockMinutes; _lockMin = shell.Settings.LockOnMinimize; _wipe = shell.Vault.WipeAfterFailures;
        Refresh = ReactiveCommand.CreateFromTask(LoadDevicesAsync);
        Revoke = ReactiveCommand.CreateFromTask<DeviceItem>(RevokeAsync);
        SetPin = ReactiveCommand.CreateFromTask(SetPinAsync);
        RemovePin = ReactiveCommand.CreateFromTask(RemovePinAsync);
        SaveLock = ReactiveCommand.Create(SaveLockSettings);
        SignOut = ReactiveCommand.CreateFromTask(shell.SignOutAsync);
    }

    public string Username => _s.Data.Username;
    public string Server => _shell.Settings.ServerUrl;
    public bool PinEnabled => _shell.Vault.HasPin;
    public string Pin { get => _pin; set => this.RaiseAndSetIfChanged(ref _pin, value); }
    public string PinRepeat { get => _pin2; set => this.RaiseAndSetIfChanged(ref _pin2, value); }
    public string Info { get => _info; set => this.RaiseAndSetIfChanged(ref _info, value); }
    public int AutoLockMinutes { get => _autoLock; set => this.RaiseAndSetIfChanged(ref _autoLock, value); }
    public bool LockOnMinimize { get => _lockMin; set => this.RaiseAndSetIfChanged(ref _lockMin, value); }
    public bool WipeAfterFailures { get => _wipe; set => this.RaiseAndSetIfChanged(ref _wipe, value); }

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<DeviceItem, Unit> Revoke { get; }
    public ReactiveCommand<Unit, Unit> SetPin { get; }
    public ReactiveCommand<Unit, Unit> RemovePin { get; }
    public ReactiveCommand<Unit, Unit> SaveLock { get; }
    public ReactiveCommand<Unit, Unit> SignOut { get; }

    private Task LoadDevicesAsync() => RunAsync(async () =>
    {
        var r = await _s.Api.CallAsync(h => _s.Api.Devices.ListDevicesAsync(new Empty(), h).ResponseAsync);
        Devices.Clear();
        foreach (var d in r.Devices) Devices.Add(new DeviceItem(d));
    });

    private Task RevokeAsync(DeviceItem d) => RunAsync(async () =>
    {
        await _s.Api.CallAsync(h => _s.Api.Devices.RevokeDeviceAsync(new RevokeDeviceRequest { DeviceId = d.Device.DeviceId }, h).ResponseAsync);
        Devices.Remove(d);
        Info = "Устройство отключено.";
    });

    private Task SetPinAsync() => RunAsync(async () =>
    {
        Info = "";
        if (Pin.Length < 6 || !Pin.All(char.IsDigit)) { Error = "PIN должен состоять не менее чем из 6 цифр."; return; }
        if (Pin != PinRepeat) { Error = "PIN-коды не совпадают."; return; }
        await _shell.Vault.SetPinAsync(_s.Ldk, Pin);
        Pin = PinRepeat = "";
        this.RaisePropertyChanged(nameof(PinEnabled));
        Info = "PIN установлен. После блокировки можно вводить PIN или пароль.";
    });

    private Task RemovePinAsync() => RunAsync(async () =>
    {
        _shell.Vault.RemovePin();
        this.RaisePropertyChanged(nameof(PinEnabled));
        Info = "PIN удалён: для разблокировки нужен пароль.";
        await Task.CompletedTask;
    });

    private void SaveLockSettings()
    {
        _shell.Settings.AutoLockMinutes = Math.Clamp(AutoLockMinutes, 0, 240);
        _shell.Settings.LockOnMinimize = LockOnMinimize;
        _shell.Settings.Save();
        _shell.Vault.SetWipeAfterFailures(WipeAfterFailures);
        Info = "Настройки сохранены.";
    }
}
