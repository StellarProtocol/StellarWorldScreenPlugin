//! stellar-castbox — video helper for StellarWorldScreen.
//!
//! Streams raw video frames to the in-game plugin over localhost TCP (see `../docs/protocol.md`).
//! This is the Milestone-A stub; the server + sources land in later tasks.

mod wire;

fn main() {
    eprintln!("stellar-castbox {}", env!("CARGO_PKG_VERSION"));
}
