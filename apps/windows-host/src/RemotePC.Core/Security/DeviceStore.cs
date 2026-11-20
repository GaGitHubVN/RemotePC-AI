using System.Text.Json;
using System.Text.Json.Serialization;
using RemotePC.Core.Protocol;

namespace RemotePC.Core.Security;

/// <summary>Encrypts data at rest. Windows implementation uses DPAPI (CurrentUser).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext, string purpose);
    byte[] Unprotect(byte[] protectedData, string purpose);
}

public sealed record PairedDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public required string EdPk { get; init; }
    public required string XPk { get; init; }
    public Permission Permissions { get; init; }
    public DateTimeOffset PairedAt { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
}

internal sealed record DeviceFile
{
    public int Version { get; init; } = 1;
    public List<PairedDevice> Devices { get; init; } = [];
    public int MaxConcurrentSessions { get; init; } = 2;
}

/// <summary>Paired devices persisted encrypted in a single file. Thread-safe.</summary>
public sealed class DeviceStore
{
    private const string Purpose = "remotepc.devices.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly Lock _lock = new();
    private DeviceFile _data;

    public event Action? Changed;

    public DeviceStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
        _data = Load();
    }

    private DeviceFile Load()
    {
        if (!File.Exists(_path)) return new DeviceFile();
        var plain = _protector.Unprotect(File.ReadAllBytes(_path), Purpose);
        return JsonSerializer.Deserialize<DeviceFile>(plain, Json) ?? new DeviceFile();
    }

    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_data, Json);
        var tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(tmp, _protector.Protect(bytes, Purpose));
        File.Move(tmp, _path, overwrite: true);
    }

    public IReadOnlyList<PairedDevice> All
    {
        get { lock (_lock) return _data.Devices.ToList(); }
    }

    public int MaxConcurrentSessions
    {
        get { lock (_lock) return _data.MaxConcurrentSessions; }
        set
        {
            lock (_lock) { _data = _data with { MaxConcurrentSessions = Math.Clamp(value, 1, 8) }; Save(); }
            Changed?.Invoke();
        }
    }

    public PairedDevice? Get(string id)
    {
        lock (_lock) return _data.Devices.FirstOrDefault(d => d.Id == id);
    }

    public void Upsert(PairedDevice device)
    {
        if (!Crypto.IsDeviceId(device.Id)) throw new ArgumentException("invalid device id");
        if (Crypto.DeviceIdOf(Enc.FromB64u(device.EdPk)) != device.Id) throw new ArgumentException("id does not match key");
        lock (_lock)
        {
            _data.Devices.RemoveAll(d => d.Id == device.Id);
            _data.Devices.Add(device);
            Save();
        }
        Changed?.Invoke();
    }

    public void Update(string id, Func<PairedDevice, PairedDevice> change)
    {
        lock (_lock)
        {
            var i = _data.Devices.FindIndex(d => d.Id == id);
            if (i < 0) return;
            var updated = change(_data.Devices[i]);
            if (updated.Id != id || updated.EdPk != _data.Devices[i].EdPk || updated.XPk != _data.Devices[i].XPk)
                throw new InvalidOperationException("device identity is immutable");
            _data.Devices[i] = updated;
            Save();
        }
        Changed?.Invoke();
    }

    public bool Revoke(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _data.Devices.RemoveAll(d => d.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void RevokeAll()
    {
        lock (_lock) { _data.Devices.Clear(); Save(); }
        Changed?.Invoke();
    }
}

/// <summary>Loads or creates this machine's identity keys, encrypted at rest.</summary>
public static class IdentityStore
{
    private const string Purpose = "remotepc.identity.v1";

    public static DeviceKeys LoadOrCreate(string path, ISecretProtector protector)
    {
        if (File.Exists(path))
        {
            var secret = protector.Unprotect(File.ReadAllBytes(path), Purpose);
            try { return DeviceKeys.ImportSecret(secret); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
        }
        var keys = DeviceKeys.Generate();
        var raw = keys.ExportSecret();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, protector.Protect(raw, Purpose));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw); }
        return keys;
    }
}
