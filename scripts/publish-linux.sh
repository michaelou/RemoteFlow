#!/usr/bin/env bash
#
# Builds the Linux release artefacts: a self-contained publish per architecture, a portable tar.gz, and a
# Debian package. The counterpart to publish-windows.ps1.
#
# Run from anywhere; paths are resolved from the script's own location. Output lands in artifacts/, which is
# git-ignored.
#
# Self-contained means the tarball runs on a machine with no .NET installed. Trimming and AOT stay off: EF
# Core and Avalonia's XAML loader rely on reflection a trimmer cannot follow.
#
# The embedded RDP control is Windows-only, so unlike the Windows script this one does not look for
# RemoteFlow.Rdp.Windows.dll — a Linux publish legitimately lacks it.

set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repository_root/src/RemoteFlow.Desktop/RemoteFlow.Desktop.csproj"
packaging="$repository_root/build/linux"

configuration='Release'
output_directory="$repository_root/artifacts"
keep_publish_output=0
runtimes=()
# Debian wants a human and an address. Override for a fork rather than shipping someone else's name.
maintainer="${REMOTEFLOW_DEB_MAINTAINER:-michaelou <andreas.michaelou@live.com>}"

usage() {
    cat <<'USAGE'
Usage: publish-linux.sh [options]

  --runtime <rid>          linux-x64 or linux-arm64. Repeatable. Both by default.
  --configuration <name>   Build configuration. Release by default.
  --output-directory <dir> Where artefacts land. <repo>/artifacts by default.
  --keep-publish-output    Leave the intermediate publish directories in place for inspection.
  -h, --help               Show this help.

Environment:
  REMOTEFLOW_DEB_MAINTAINER  Overrides the Debian Maintainer field.
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --runtime)
            case "${2:-}" in
                linux-x64 | linux-arm64) runtimes+=("$2") ;;
                *) echo "error: --runtime must be linux-x64 or linux-arm64, got '${2:-}'." >&2; exit 2 ;;
            esac
            shift 2
            ;;
        --configuration) configuration="${2:?--configuration needs a value}"; shift 2 ;;
        --output-directory) output_directory="${2:?--output-directory needs a value}"; shift 2 ;;
        --keep-publish-output) keep_publish_output=1; shift ;;
        -h | --help) usage; exit 0 ;;
        *) echo "error: unknown argument '$1'." >&2; usage >&2; exit 2 ;;
    esac
done

if [[ ${#runtimes[@]} -eq 0 ]]; then
    runtimes=(linux-x64 linux-arm64)
fi

for tool in dotnet dpkg-deb tar; do
    command -v "$tool" >/dev/null 2>&1 || { echo "error: '$tool' is required but not installed." >&2; exit 1; }
done

publish_root="$output_directory/publish"
mkdir -p "$output_directory"

# The apphost is the only place the version exists on Linux. publish-windows.ps1 reads
# VersionInfo.ProductVersion off the PE header; ELF has no equivalent, so run the binary instead and parse
# `RemoteFlow <version> (commit <sha>)`. Either way MinVer stays the single source of truth.
read_version() {
    local apphost="$1" reported
    reported="$("$apphost" --version 2>/dev/null)" || {
        echo "error: '$apphost --version' failed. Is MinVer wired in?" >&2
        return 1
    }
    reported="$(awk '{print $2}' <<<"$reported")"
    [[ -n $reported ]] || { echo "error: could not parse a version out of '$apphost --version'." >&2; return 1; }
    printf '%s' "$reported"
}

# Debian orders '~' before everything, including the empty string, so a MinVer prerelease such as
# 0.2.6-alpha.0.5 must become 0.2.6~alpha.0.5 or dpkg would rank it ABOVE the 0.2.6 release it precedes.
debian_version() {
    printf '%s' "${1/-/\~}"
}

debian_arch() {
    case "$1" in
        linux-x64) printf 'amd64' ;;
        linux-arm64) printf 'arm64' ;;
        *) echo "error: no Debian architecture known for '$1'." >&2; return 1 ;;
    esac
}

host_rid() {
    case "$(uname -m)" in
        x86_64) printf 'linux-x64' ;;
        aarch64 | arm64) printf 'linux-arm64' ;;
        *) printf 'unknown' ;;
    esac
}

build_deb() {
    local rid="$1" version="$2" publish_dir="$3"
    local arch staging deb_version installed_size deb_path

    arch="$(debian_arch "$rid")"
    deb_version="$(debian_version "$version")"
    staging="$publish_root/deb-$rid"
    deb_path="$output_directory/remoteflow_${deb_version}_${arch}.deb"

    rm -rf "$staging"
    mkdir -p "$staging/DEBIAN" "$staging/opt/remoteflow" "$staging/usr/bin" \
        "$staging/usr/share/applications" "$staging/usr/share/doc/remoteflow"

    cp -a "$publish_dir/." "$staging/opt/remoteflow/"
    chmod 0755 "$staging/opt/remoteflow/RemoteFlow"

    ln -s /opt/remoteflow/RemoteFlow "$staging/usr/bin/remoteflow"
    install -m 0644 "$packaging/remoteflow.desktop" "$staging/usr/share/applications/remoteflow.desktop"
    install -m 0644 "$repository_root/LICENSE" "$staging/usr/share/doc/remoteflow/copyright"

    local icon size
    for icon in "$packaging"/icons/remoteflow-*.png; do
        size="$(basename "$icon" .png)"
        size="${size#remoteflow-}"
        install -D -m 0644 "$icon" \
            "$staging/usr/share/icons/hicolor/${size}x${size}/apps/remoteflow.png"
    done

    # dpkg reports Installed-Size in KiB, and it is the staged tree minus the control metadata.
    installed_size="$(du -sk --exclude=DEBIAN "$staging" | cut -f1)"

    sed -e "s|@VERSION@|$deb_version|g" \
        -e "s|@ARCH@|$arch|g" \
        -e "s|@MAINTAINER@|$maintainer|g" \
        -e "s|@INSTALLED_SIZE@|$installed_size|g" \
        "$packaging/control.in" >"$staging/DEBIAN/control"

    # --root-owner-group, or every file in the package is owned by whoever ran this script. No maintainer
    # scripts: desktop-file-utils and hicolor-icon-theme ship dpkg triggers that refresh the caches.
    dpkg-deb --build --root-owner-group -Zxz "$staging" "$deb_path" >/dev/null

    [[ $keep_publish_output -eq 1 ]] || rm -rf "$staging"
    printf '%s' "$deb_path"
}

echo "Repository:    $repository_root"
echo "Configuration: $configuration"
echo "Output:        $output_directory"
echo

for rid in "${runtimes[@]}"; do
    echo "=== $rid ==="
    publish_dir="$publish_root/$rid"
    rm -rf "$publish_dir"

    dotnet publish "$project" \
        --configuration "$configuration" \
        --runtime "$rid" \
        --self-contained true \
        --output "$publish_dir"

    apphost="$publish_dir/RemoteFlow"
    [[ -f $apphost ]] || { echo "error: expected an apphost at '$apphost'." >&2; exit 1; }
    chmod +x "$apphost"

    if [[ "$rid" == "$(host_rid)" ]]; then
        version="$(read_version "$apphost")"
        reported="$version"
    else
        # Cross-architecture: the binary cannot run here, so fall back to MinVer via MSBuild. The Windows
        # script reports "skipped (cross-architecture)" for the same reason.
        #
        # -target:MinVer is load-bearing. MinVer assigns Version from a target, so evaluating the property
        # on its own reports the SDK's default 1.0.0 and silently mis-stamps the package.
        version="$(dotnet msbuild "$project" -target:MinVer -getProperty:Version -nologo | tr -d '[:space:]')"
        reported='skipped (cross-architecture)'
    fi
    [[ -n $version ]] || { echo "error: no version resolved for $rid." >&2; exit 1; }

    tarball="$output_directory/RemoteFlow-$version-$rid.tar.gz"
    rm -f "$tarball"
    tar -czf "$tarball" -C "$publish_dir" .

    deb_path="$(build_deb "$rid" "$version" "$publish_dir")"

    [[ $keep_publish_output -eq 1 ]] || rm -rf "$publish_dir"

    echo
    echo "Runtime:  $rid"
    echo "Version:  $version"
    echo "Reported: $reported"
    echo "Tarball:  $tarball"
    echo "Package:  $deb_path"
    echo
done

[[ $keep_publish_output -eq 1 ]] || rmdir "$publish_root" 2>/dev/null || true
