#!/usr/bin/env bash
set -e

DIR=$( cd -- "$( dirname -- "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )

dotnet build --configuration Release

for i in IMGUI*/; do
    name="${i%/}"

    tmp=$(mktemp -d)
    cd $tmp

    mkdir -p BepInEx/patchers
    cp "$DIR/artifacts/bin/$name/release/"*.dll ./BepInEx/patchers

    tar -a -cf "$DIR/artifacts/$name.zip" ./
done
