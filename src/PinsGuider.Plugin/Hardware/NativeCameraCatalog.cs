// SPDX-License-Identifier: MPL-2.0

using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyCamera.ToupTekAlike;
using NINA.Equipment.Interfaces;
using NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK;
using NINA.Equipment.SDK.CameraSDKs.SVBonySDK;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using QHYCCD;

namespace PinsGuider.Plugin.Hardware;

/// <summary>Native SDK discovery using PINS' existing camera adapters. Discovery never connects a camera.</summary>
internal static class NativeCameraCatalog
{
    public const string Prefix = "sdk:";
    public static bool IsNative(string driver) => driver.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ICamera> GetCameras(string driver, IProfileService profile, IExposureDataFactory factory)
    {
        var cameras = new List<ICamera>();
        switch (driver.ToLowerInvariant())
        {
            case "sdk:asi":
                for (int i = 0, count = ASICameras.Count; i < count; i++) cameras.Add(ASICameras.GetCamera(i, profile, factory));
                break;
            case "sdk:qhy":
                var qhy = new QHYCameras(factory);
                for (uint i = 0, count = qhy.Count; i < count; i++) cameras.Add(qhy.GetCamera(i, profile));
                break;
            case "sdk:playerone":
                cameras.AddRange(new PlayerOneProvider(profile, factory).GetEquipment());
                break;
            case "sdk:svbony-legacy":
                cameras.AddRange(new SVBonyProvider(profile, factory).GetEquipment());
                break;
            case "sdk:svbony":
                AddToup(Svbonycam.EnumV2().Select(d => d.ToDeviceInfo()), () => new SVBonySDKWrapper());
                break;
            case "sdk:touptek":
                AddToup(ToupTek.ToupCam.EnumV2().Select(d => d.ToDeviceInfo()), () => new ToupTekSDKWrapper());
                break;
            case "sdk:altair":
                AddToup(Altair.Altaircam.EnumV2().Select(d => d.ToDeviceInfo()), () => new AltairSDKWrapper());
                break;
            case "sdk:ogma":
                AddToup(Ogmacam.EnumV2().Select(d => d.ToDeviceInfo()), () => new OgmaSDKWrapper());
                break;
            case "sdk:omegon":
                AddToup(Omegon.Omegonprocam.EnumV2().Select(d => d.ToDeviceInfo()), () => new OmegonSDKWrapper());
                break;
            case "sdk:risingcam":
                AddToup(Nncam.EnumV2().Select(d => d.ToDeviceInfo()), () => new RisingcamSDKWrapper());
                break;
            case "sdk:mallincam":
                AddToup(MallinCam.Mallincam.EnumV2().Select(d => d.ToDeviceInfo()), () => new MallinCamSDKWrapper());
                break;
            default:
                throw new PinsGuider.Engine.Core.GuideCameraException($"Unknown native camera SDK '{driver}'.");
        }
        return cameras;

        void AddToup(IEnumerable<ToupTekAlikeDeviceInfo> devices, Func<IToupTekAlikeCameraSDK> sdk)
        {
            foreach (var device in devices)
            {
                var flags = (ToupTekAlikeFlag)device.model.flag;
                if ((flags & (ToupTekAlikeFlag.FLAG_FILTERWHEEL | ToupTekAlikeFlag.FLAG_AUTOFOCUSER)) != 0) continue;
                cameras.Add(new ToupTekAlikeCamera(device, sdk(), profile, factory));
            }
        }
    }

    // Existing contract returns strings: keep the persistent device ID in the selection, including identical models.
    public static string Selection(ICamera camera) => $"{camera.Name} [{camera.Id}]";
}
