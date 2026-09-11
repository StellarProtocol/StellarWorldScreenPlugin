//! Library surface for `stellar-castbox`, shared by the binary (`main.rs`) and the integration test
//! (`tests/pipe.rs`) — a `tests/` file compiles as its own crate and can only see items reachable
//! through a crate's public library API, so `wire`/`source`/`server` are exposed here as `pub mod`.
//! See `docs/protocol.md` for the wire contract this implements.

pub mod server;
pub mod source;
pub mod wire;
