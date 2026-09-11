//! Frame source abstraction. A `Source` describes its stream (`info`) and produces frames one at a
//! time (`next_frame`), pacing itself to its own fps. `server::serve` drives a `Source` generically
//! (never as `dyn Source`) — Milestone A has exactly one live source, so the native `async fn` below
//! doesn't need to be object-safe.

pub mod testpattern;

/// Stream metadata sent to the plugin as STREAM_INFO right after HELLO (`docs/protocol.md`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StreamInfo {
    pub w: u16,
    pub h: u16,
    pub pixfmt: u8,
    pub fps: u8,
    pub title: String,
}

/// A source of video frames for the frame server (`server::serve`) to stream out.
pub trait Source: Send {
    /// Stream metadata, sent once as STREAM_INFO before the first frame.
    fn info(&self) -> StreamInfo;

    /// Produces the next frame as `(pts_ms, rgba_bytes)`, pacing itself to its own fps. Returns
    /// `None` when the source is exhausted (never for `TestPattern`, which runs forever).
    ///
    /// Written as `-> impl Future<..> + Send` rather than plain `async fn` in the trait: bare
    /// `async fn` in a trait can't express a `Send` bound on the returned future (rustc's own
    /// `async_fn_in_trait` lint flags this), which matters once a caller wants the future crossing
    /// an `.await` in code that might be spawned across threads. Implementations may still just
    /// write `async fn next_frame(...) { .. }` — that satisfies this signature as long as the
    /// concrete future is actually `Send`, true here since `Source: Send`.
    fn next_frame(&mut self) -> impl std::future::Future<Output = Option<(u64, Vec<u8>)>> + Send;
}
