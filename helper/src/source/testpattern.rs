//! A generated moving-gradient test pattern — Milestone A's frame source, used to prove the wire
//! pipe end-to-end before any real decoder exists.

use super::{Source, StreamInfo};

/// Generates a moving diagonal-gradient RGBA test pattern at a fixed size/fps. The pixel buffer is
/// reused across frames (`buf`) — only the `.clone()` handed to the wire layer per frame allocates.
pub struct TestPattern {
    w: u16,
    h: u16,
    fps: u8,
    phase: u8,
    pts_ms: u64,
    buf: Vec<u8>,
}

impl TestPattern {
    pub fn new(w: u16, h: u16, fps: u8) -> Self {
        Self { w, h, fps, phase: 0, pts_ms: 0, buf: vec![0u8; w as usize * h as usize * 4] }
    }

    /// Repaints `self.buf` as a diagonal gradient offset by `self.phase`, then advances the phase
    /// so the next call produces a visibly different (moving) frame.
    fn paint(&mut self) {
        let (w, h, phase) = (self.w as usize, self.h as usize, self.phase);
        for y in 0..h {
            for x in 0..w {
                let i = (y * w + x) * 4;
                let v = ((x + y) as u8).wrapping_add(phase);
                self.buf[i] = v;
                self.buf[i + 1] = v.wrapping_add(85);
                self.buf[i + 2] = v.wrapping_add(170);
                self.buf[i + 3] = 255;
            }
        }
        self.phase = self.phase.wrapping_add(1);
    }
}

impl Source for TestPattern {
    fn info(&self) -> StreamInfo {
        StreamInfo { w: self.w, h: self.h, pixfmt: 0, fps: self.fps, title: "test pattern".into() }
    }

    async fn next_frame(&mut self) -> Option<(u64, Vec<u8>)> {
        self.paint();
        let frame_ms = 1000 / self.fps as u64;
        tokio::time::sleep(std::time::Duration::from_millis(frame_ms)).await;
        let pts = self.pts_ms;
        self.pts_ms += frame_ms;
        Some((pts, self.buf.clone()))
    }
}

#[cfg(test)]
mod tests {
    use super::super::Source;
    use super::TestPattern;

    #[tokio::test]
    async fn next_frame_is_reused_buffer_sized_w_h_4() {
        let mut tp = TestPattern::new(4, 2, 30);
        let (_pts, frame) = tp.next_frame().await.unwrap();
        assert_eq!(frame.len(), 4 * 2 * 4);
    }

    #[tokio::test]
    async fn next_frame_pts_advances_by_1000_over_fps_each_call() {
        let mut tp = TestPattern::new(4, 2, 30);
        let (pts1, _) = tp.next_frame().await.unwrap();
        let (pts2, _) = tp.next_frame().await.unwrap();
        assert_eq!(pts2 - pts1, 1000 / 30);
    }

    #[tokio::test]
    async fn next_frame_pixels_change_between_calls_motion_is_visible() {
        let mut tp = TestPattern::new(4, 2, 30);
        let (_, frame1) = tp.next_frame().await.unwrap();
        let (_, frame2) = tp.next_frame().await.unwrap();
        assert_ne!(frame1, frame2, "phase must advance so the pattern visibly moves");
    }

    #[tokio::test]
    async fn info_reports_configured_dimensions_and_rgba_pixfmt() {
        let tp = TestPattern::new(640, 360, 30);
        let info = tp.info();
        assert_eq!(info.w, 640);
        assert_eq!(info.h, 360);
        assert_eq!(info.pixfmt, 0);
        assert_eq!(info.fps, 30);
    }
}
