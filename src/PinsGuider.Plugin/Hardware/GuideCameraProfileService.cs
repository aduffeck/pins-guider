// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace PinsGuider.Plugin.Hardware;

/// <summary>Private, in-memory settings for SDK cameras. No imaging profile is selected, mutated or saved.</summary>
internal sealed class GuideCameraProfileService : IProfileService, IDisposable
{
    public IProfile ActiveProfile { get; } = new Profile();
    public AsyncObservableCollection<ProfileMeta> Profiles { get; } = [];
    public bool ProfileWasSpecifiedFromCommandLineArgs => false;

    public GuideCameraProfileService()
    {
        ActiveProfile.CameraSettings.BitScaling = false;
    }

    public bool Clone(ProfileMeta profileInfos) => throw new NotSupportedException();
    public void Add() => throw new NotSupportedException();
    public bool SelectProfile(ProfileMeta profileInfo) => throw new NotSupportedException();
    public bool RemoveProfile(ProfileMeta profileInfo) => throw new NotSupportedException();
    public void ChangeLocale(CultureInfo language) => throw new NotSupportedException();
    public void ChangeLatitude(double latitude) => throw new NotSupportedException();
    public void ChangeLongitude(double longitude) => throw new NotSupportedException();
    public void ChangeElevation(double elevation) => throw new NotSupportedException();
    public void ChangeHorizon(string horizonFilePath) => throw new NotSupportedException();
    public void Release() { }
    public void Dispose() => ActiveProfile.Dispose();

    public event EventHandler? LocaleChanged { add { } remove { } }
    public event EventHandler? LocationChanged { add { } remove { } }
    public event EventHandler? BeforeProfileChanging { add { } remove { } }
    public event EventHandler? ProfileChanged { add { } remove { } }
    public event EventHandler? HorizonChanged { add { } remove { } }
}
