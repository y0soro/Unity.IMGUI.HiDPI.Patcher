#!/usr/bin/env bash
set -e

DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)

dotnet build --configuration Release /p:ContinuousIntegrationBuild=true

version=$(sed -n 's/.*<Version>\(.*\)<.*/\1/p' Directory.Build.props | tr -d '\n')

for i in IMGUI*/; do
    name="${i%/}"

    tmp=$(mktemp -d)
    cd $tmp

    install -TD "${DIR}/docs/IMGUI.HiDPI.Patcher.cfg" "./BepInEx/config/${name}.cfg"
    install -D "${DIR}/artifacts/bin/${name}/release/"*.dll -t ./BepInEx/patchers

    tar --sort=name --owner=root:0 --group=root:0 --mtime='UTC 1970-01-01' \
        -a -cf "$DIR/artifacts/${name}-v${version}.zip" ./
done
