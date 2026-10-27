# Third-party notices

Soulstone Coop's own code is under the [MIT License](LICENSE). The release zip also bundles, or the mod depends on, the following third-party software, each under its own license:

| Component | Use | License | Source |
|---|---|---|---|
| BepInEx 6 (be.789, IL2CPP) | Mod loader, bundled in the release zip (`winhttp.dll`, `doorstop_config.ini`, `dotnet/`, `BepInEx/core/`) | LGPL-2.1 | https://github.com/BepInEx/BepInEx |
| Unity Doorstop | Loader entry point shipped with BepInEx (`winhttp.dll`) | LGPL-2.1 | https://github.com/NeighTools/UnityDoorstop |
| Il2CppInterop | IL2CPP interop runtime, bundled with BepInEx | LGPL-3.0 | https://github.com/BepInEx/Il2CppInterop |
| HarmonyX | Runtime patching, bundled with BepInEx | MIT | https://github.com/BepInEx/HarmonyX |
| .NET runtime | CoreCLR runtime used by BepInEx IL2CPP, bundled in `dotnet/` | MIT | https://github.com/dotnet/runtime |
| LiteNetLib 1.3.1 | Direct-IP transport, bundled in `BepInEx/plugins/SoulstoneCoop/` | MIT | https://github.com/RevenantX/LiteNetLib |

Those components are distributed unmodified; their source code and full license texts are available at the links above.

Soulstone Survivors and all of its code, assets and trademarks belong to their respective owners. Nothing from the game is included in this repository or in the release zip; the interop assemblies BepInEx generates from the game on first launch stay on the player's machine. Soulstone Coop is an unofficial fan mod, not affiliated with or endorsed by the game's developers.
