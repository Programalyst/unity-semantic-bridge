#!/usr/bin/env bash
set -euo pipefail
usb_repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
usb_editor="${USB_UNITY_EDITOR:-/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity}"
usb_project="$usb_repo_root/TestProject"
usb_results="$usb_project/TestResults"
if [[ ! -x "$usb_editor" ]]; then
    printf 'Set USB_UNITY_EDITOR to an installed Unity executable.\n' >&2
    exit 1
fi
mkdir -p "$usb_results"
printf 'Preparing USB fixtures in %s\n' "$usb_project"
"$usb_editor" -batchmode -projectPath "$usb_project" -executeMethod UsbTestProjectSetup.Prepare -logFile "$usb_results/setup.log"
printf 'Running USB EditMode tests; logs/results: %s\n' "$usb_results"
# Pass -testFilter yourself to select a subset. No -nographics: UI tests render.
"$usb_editor" -batchmode -projectPath "$usb_project" -runTests -testPlatform EditMode -testResults "$usb_results/editmode.xml" -logFile "$usb_results/editmode.log" "$@"
