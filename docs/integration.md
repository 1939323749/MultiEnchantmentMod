# Integrating MultiEnchantmentMod into your mod

This doc covers how to reference MultiEnchantmentMod's API from a downstream Slay the Spire 2
mod project. There is no public NuGet package today — the framework ships as a Godot mod and
its `.dll` lives in the user's `mods/MultiEnchantmentMod/` folder at game runtime.

Two integration patterns are supported. Pick one.

---

## Option A — Reference the installed `.dll` (recommended for end-user mods)

If you are shipping a mod that targets MultiEnchantmentMod as a runtime dependency, this is the
cleanest path. You don't redistribute the .dll; the user is expected to install
MultiEnchantmentMod separately.

```xml
<Project Sdk="Godot.NET.Sdk/4.5.1">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- MultiEnchantmentMod installed alongside your mod under <STS2>/mods/ -->
  <PropertyGroup>
    <MultiEnchantmentModPath>$(ModsPath)/MultiEnchantmentMod/MultiEnchantmentMod.dll</MultiEnchantmentModPath>
  </PropertyGroup>

  <ItemGroup Condition="Exists('$(MultiEnchantmentModPath)')">
    <Reference Include="MultiEnchantmentMod">
      <HintPath>$(MultiEnchantmentModPath)</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <Target Name="CheckMultiEnchantmentMod" BeforeTargets="Build"
          Condition="!Exists('$(MultiEnchantmentModPath)')">
    <Error Text="MultiEnchantmentMod not found at $(MultiEnchantmentModPath). Install it from Steam Workshop or copy the build into mods/MultiEnchantmentMod/." />
  </Target>
</Project>
```

`<Private>false</Private>` is important — it prevents `dotnet publish` from copying
MultiEnchantmentMod.dll into your own mod's output. The user has one copy in the mods folder;
your mod just references it for type resolution.

---

## Option B — Source-clone reference (recommended for in-repo development)

If you are developing MultiEnchantmentMod itself or a tightly coupled companion mod and want
project-level integration:

```xml
<ItemGroup>
  <ProjectReference Include="../MultiEnchantmentMod/MultiEnchantmentMod.csproj">
    <Private>false</Private>
    <ReferenceOutputAssembly>true</ReferenceOutputAssembly>
  </ProjectReference>
</ItemGroup>
```

`<Private>false</Private>` again prevents the .dll from being copied — the game will load it
from the installed `mods/MultiEnchantmentMod/` folder, not from your mod's directory.

---

## Required: declare API compatibility

Add this once, anywhere in your assembly (typically `AssemblyInfo.cs` or any `.cs` file outside
a namespace declaration):

```csharp
using MultiEnchantmentMod.Api;
[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]
```

The analyzer (rule **MEM007**, warning level since the v2 stabilization round) flags assemblies
that omit this tag. At runtime, the assembly scanner consults the tag and refuses to scan
assemblies whose declared `MinVersion` exceeds the framework's current version — keeping you
from registering against an incompatible API.

## Required: runtime version check

In your `[ModInitializer]`:

```csharp
public static void Initialize()
{
    if (!MultiEnchantmentApi.RequireApiVersion(MultiEnchantmentApiVersion.Current))
    {
        // Logged for the user; bail out before registering anything.
        return;
    }

    MultiEnchantmentApi.ScanCallingAssembly();
    // ...rest of your init...
}
```

`RequireApiVersion` logs to `godot.log` and returns `false` on mismatch — don't
proceed with registration in that case.

## Analyzer wiring

If you reference `MultiEnchantmentMod.dll` via Option A or B, the analyzer (`MEM001` through
`MEM013`) is **not** automatically activated — the analyzer ships as a separate
`MultiEnchantmentMod.Analyzers` assembly. To enable it during your build, add:

```xml
<ItemGroup>
  <Analyzer Include="$(MultiEnchantmentModPath)/../MultiEnchantmentMod.Analyzers.dll" />
</ItemGroup>
```

(Adjust the path to where `MultiEnchantmentMod.Analyzers.dll` actually lives.) This is opt-in;
the framework works without it, but you lose compile-time validation of `[ModifyDynamicVar]`,
`[ModifyEnergyCost]`, `[ModifyCardPlayCount]` method signatures and similar gotchas.

### Auto-fix support (IDE quick fixes)

The analyzer ships with code-fix providers for two of the most common diagnostics — Rider /
Visual Studio / VS Code will offer a one-click fix when these warnings appear:

| Diagnostic | Auto-fix |
| --- | --- |
| **MEM007** *Assembly should declare API compatibility* | Inserts `[assembly: EnchantmentApiCompatibility(2)]` (and the `using MultiEnchantmentMod.Api;` if missing) into an `AssemblyInfo.cs`-like file when present, otherwise the current file. |
| **MEM009** *[ModifyDynamicVar] method has wrong signature* | Rewrites the offending method's signature to `decimal MethodName(EnchantmentStackSnapshot snapshot, decimal currentValue)`. |

The other diagnostics (MEM001–MEM006, MEM008, MEM011, MEM012) report only — they describe
semantic mismatches a code-fix cannot safely automate. MEM013 also reports only; it catches
bad `[ModifyEnergyCost]` / `[ModifyCardPlayCount]` signatures.

## Deploying your mod's .dll (in-repo development)

If you are using **Option B** (project reference, in-repo development of MultiEnchantmentMod
itself or a tightly coupled companion), `dotnet publish` builds your `.dll` but does **not**
auto-copy it into the game's `mods/<YourMod>/` folder. Add this MSBuild target to your `.csproj`
to automate the copy step (this is the same pattern MultiEnchantmentMod's own csproj uses):

```xml
<Target Name="CopyDllToMods" AfterTargets="Publish"
        Condition="'$(ModsPath)' != '' and '$(IsInnerGodotExport)' != 'true'">
    <PropertyGroup>
        <_ModOutputDir>$(ModsPath)$(MSBuildProjectName)/</_ModOutputDir>
    </PropertyGroup>
    <MakeDir Directories="$(_ModOutputDir)" Condition="!Exists('$(_ModOutputDir)')"/>
    <Copy SourceFiles="$(PublishDir)$(MSBuildProjectName).dll"
          DestinationFolder="$(_ModOutputDir)" SkipUnchangedFiles="true"/>
    <Copy SourceFiles="$(PublishDir)$(MSBuildProjectName).pdb"
          DestinationFolder="$(_ModOutputDir)" SkipUnchangedFiles="true"
          Condition="Exists('$(PublishDir)$(MSBuildProjectName).pdb')"/>
    <Message Text="Copied $(MSBuildProjectName).dll to $(_ModOutputDir)" Importance="high"/>
</Target>
```

After this, `dotnet publish` builds + exports the `.pck` **and** drops the freshly built `.dll`
straight into the game's mods folder, so the running game picks up the new code on next launch
without any manual copy step.

## Localization keys

Place per-mod loc files under `<YourMod>/localization/<lang>/<file>.json`. Keys follow the
convention `<ENCHANTMENT_MODEL_ID>.<field>`:

```json
{
  "YOUR_ENCHANTMENT.title": "...",
  "YOUR_ENCHANTMENT.description": "...",
  "YOUR_ENCHANTMENT.extraCardText": "..."
}
```

The vanilla `LocManager` reads them at startup; no MultiEnchantmentMod API call is needed.

---

## Troubleshooting

- **`MEM007` warning**: Missing `[assembly: EnchantmentApiCompatibility(...)]`. Add it.
- **`[StackApi] Ignored scan request for <Asm>: registry is sealed.`**: Something (likely
  another mod) called `MultiEnchantmentApi.SealRegistry()` before your `[ModInitializer]` ran.
  Move your registration earlier in mod load order, or coordinate with the sealing mod.
- **`[StackApi] Late registration for <Type> ... rejected: registry is sealed.`**: Same root
  cause, but via a direct `Register<T>().Commit()` rather than scanning. Same fix.
- **No log line matches `[StackApi]` at all**: Either MultiEnchantmentMod failed to load (check
  for upstream errors in `godot.log`) or your `[ModInitializer]` never ran. Verify your mod's
  manifest dependency declaration and that the game considers your mod enabled.
