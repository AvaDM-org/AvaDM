namespace AvaDM.UI.Services;

/// <summary>Which of release.yml's published distribution formats the running build came from.
/// See <see cref="UpdateChannelDetector"/> for how each is recognized and <see cref="UpdateService"/>
/// for how each is updated.</summary>
public enum UpdateChannel
{
    WindowsInstaller,
    WindowsPortable,
    LinuxAppImage,
    LinuxDeb,
    LinuxPortable,
    MacOsDmg,
    Unknown,
}
