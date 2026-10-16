"""Installs the .NET 8 packs at exactly the versions the image's SDK asks for (its BundledVersions.props).

`p compile` emits net8.0 projects, and verify runs with the network off: a pack version the SDK
expects but the image lacks makes restore reach nuget.org (NU1301). Copying packs from the sdk:8.0
image drifts whenever either base image moves; reading the SDK's own versions does not.
"""
import glob, io, os, sys, urllib.request, xml.etree.ElementTree as ET, zipfile

ROOT = '/usr/share/dotnet'
props = sorted(glob.glob(f'{ROOT}/sdk/*/Microsoft.NETCoreSdk.BundledVersions.props'))[-1]
items = [e for e in ET.parse(props).getroot().iter() if e.get('TargetFramework') == 'net8.0']


def version(tag, include, attr):
    found = {e.get(attr) for e in items if e.tag.endswith(tag) and e.get('Include') == include and e.get(attr)}
    if len(found) != 1:
        sys.exit(f'{props}: expected one {attr} for {include} net8.0, found {sorted(found)}')
    return found.pop()


packs = {
    'Microsoft.NETCore.App.Ref': version('KnownFrameworkReference', 'Microsoft.NETCore.App', 'TargetingPackVersion'),
    'Microsoft.AspNetCore.App.Ref': version('KnownFrameworkReference', 'Microsoft.AspNetCore.App', 'TargetingPackVersion'),
    'Microsoft.NETCore.App.Host.linux-x64': version('KnownAppHostPack', 'Microsoft.NETCore.App', 'AppHostPackVersion'),
}
for name, v in packs.items():
    dest = f'{ROOT}/packs/{name}/{v}'
    if os.path.isdir(dest):
        print(f'{name} {v}: present')
        continue
    url = f'https://api.nuget.org/v3-flatcontainer/{name.lower()}/{v}/{name.lower()}.{v}.nupkg'
    with urllib.request.urlopen(url) as r:
        zipfile.ZipFile(io.BytesIO(r.read())).extractall(dest)
    print(f'{name} {v}: installed')
