# Security

## Official downloads

The only official builds of Soulstone Coop are the zips attached to this repository's [Releases](https://github.com/gskolber/soulstone-survivors-coop/releases). Each release lists the zip's SHA-256; before installing, check that

```powershell
Get-FileHash .\SoulstoneCoop-<version>.zip
```

prints the same hash. If it doesn't match, don't install it.

The code is MIT-licensed, so others may legally publish forks, re-uploads or ports (for example to MelonLoader). Those are not made, reviewed or endorsed by the author, even when they credit or link this project. If someone offers you a "co-op mod" build from another site, a Discord attachment or a direct message, treat it as untrusted.

The release zip contains only BepInEx, its runtime, this mod's two DLLs (`SoulstoneCoop.dll`, `LiteNetLib.dll`), default configs and text files. It never contains an `.exe` or an installer.

## Reporting a problem

- Security issues (for example, something in the network code that a remote player could abuse): use GitHub's [private vulnerability reporting](https://github.com/gskolber/soulstone-survivors-coop/security/advisories/new) instead of a public issue.
- A malicious copy pretending to be this mod: open a regular issue with where you found it.
