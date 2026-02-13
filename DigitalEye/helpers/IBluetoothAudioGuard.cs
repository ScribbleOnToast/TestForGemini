namespace DigitalEye.Helpers;

public interface IBluetoothAudioGuard
{
    string? EnsureInputReady();
    void StartShadowMonitor(string cardName);
    void StopShadowMonitor();
}
