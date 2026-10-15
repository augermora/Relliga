# Relliga distribution notices

Relliga is GNU GPLv3 software, provided without warranty. See LICENSE.
Source: https://github.com/augermora/Relliga
These unsigned development artifacts have not been published. Before external
binary distribution, provide the complete corresponding Relliga source for the
exact build, including modifications and build scripts, under GPLv3 section 6.
A link to a different or older source revision is not sufficient.

The portable build includes .NET (MIT), Avalonia (MIT), Inter fonts (SIL OFL),
CommunityToolkit.Mvvm (MIT), Microsoft.Extensions libraries (MIT),
Microsoft.Data.Sqlite and SQLitePCLRaw (MIT/Apache-2.0), SQLite (public domain),
Ical.Net (MIT), NodaTime (Apache-2.0), SkiaSharp/HarfBuzzSharp (MIT) and their
native components. Native-component and runtime third-party notices accompany
the distribution. The packaging script includes the dependency inventory and
license/notice files from the exact restored NuGet packages in licenses/.
License terms in those files take precedence over this summary.

Optional Windows installer: NSIS, using its zlib compression module. NSIS core
and zlib compression use the zlib/libpng license:
https://nsis.sourceforge.io/License
No NSIS compiler or build tools are bundled with the application. The installer
uses only current-user installation and never deletes the personal library.

Relliga is not affiliated with Spotify, Google, Microsoft, or Avalonia.

Optional live Telegram support uses the official TDLib JSON C ABI. Native TDLib
is not bundled in this checkout's generated packages. A live-enabled publisher
must build/provision its native library and OpenSSL/zlib dependencies, include
their exact notices, and comply with Telegram's API terms. TDLib uses BSL-1.0;
the new Microsoft ProtectedData managed dependency uses MIT terms. No account
credentials or session state may be redistributed with application packages.
