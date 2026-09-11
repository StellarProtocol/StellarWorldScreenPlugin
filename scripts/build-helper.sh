#!/usr/bin/env bash
# Cross-compile stellar-castbox to a Windows .exe (sudo-free, via zig).
# Plain `cargo build --target x86_64-pc-windows-gnu` would need a mingw linker we do NOT have;
# cargo-zigbuild provides the Windows linker through zig.
set -euo pipefail
export PATH="$HOME/.cargo/bin:$HOME/.local/bin:$PATH"
cd "$(dirname "$0")/../helper"
cargo zigbuild --release --target x86_64-pc-windows-gnu
EXE="target/x86_64-pc-windows-gnu/release/stellar-castbox.exe"
ls -la "$EXE"
file "$EXE"
