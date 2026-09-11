//! Wire codec for the plugin <-> helper protocol.
//!
//! Mirrors `docs/protocol.md` byte-for-byte — this is the Rust half of the codec; the C# half is
//! `plugin/Net/WireCodec.cs` (built in a later task). Any field change must update both in the same
//! commit. Little-endian throughout. Envelope: `[u32 len_le][u8 type][payload...]` where `len` counts
//! the bytes of `(type + payload)`.

use std::io;
use tokio::io::{AsyncRead, AsyncReadExt};

/// Maximum allowed message length (the `[u32 len]` prefix, counting `type` + payload). Guards
/// `read_msg` against allocating an unbounded buffer from a corrupt/hostile length prefix on the
/// wire — comfortably larger than a default 640×360 RGBA FRAME (~922 KB) with headroom for bigger
/// resolutions. Not a formatted part of the wire bytes; purely a receiver-side sanity cap.
pub const MAX_MSG_LEN: usize = 64 * 1024 * 1024; // 64 MiB

/// Message type byte, `docs/protocol.md` table.
pub const TYPE_HELLO: u8 = 0x01;
pub const TYPE_STREAM_INFO: u8 = 0x02;
pub const TYPE_FRAME: u8 = 0x03;
pub const TYPE_STATUS: u8 = 0x04;
pub const TYPE_CONTROL: u8 = 0x05;

/// One framed message on the wire (see `docs/protocol.md`).
#[derive(Debug, Clone, PartialEq)]
pub enum Msg {
    Hello { proto: u16, flags: u8 },
    StreamInfo { w: u16, h: u16, pixfmt: u8, fps: u8, title: String },
    Frame { pts_ms: u64, bytes: Vec<u8> },
    Status { state: u8, position_ms: u64, duration_ms: u64, err: String },
    Control(Control),
}

/// CONTROL (0x05) op payloads, `docs/protocol.md` § CONTROL ops.
#[derive(Debug, Clone, PartialEq)]
pub enum Control {
    Play,
    Pause,
    Stop,
    Seek(u64),
    Volume(u8),
    Load(String),
}

/// Encodes a full framed message: `[u32 len_le][u8 type][payload...]`.
pub fn encode(m: &Msg) -> Vec<u8> {
    let mut body = Vec::new();
    match m {
        Msg::Hello { proto, flags } => {
            body.push(TYPE_HELLO);
            body.extend_from_slice(&proto.to_le_bytes());
            body.push(*flags);
        }
        Msg::StreamInfo { w, h, pixfmt, fps, title } => {
            body.push(TYPE_STREAM_INFO);
            body.extend_from_slice(&w.to_le_bytes());
            body.extend_from_slice(&h.to_le_bytes());
            body.push(*pixfmt);
            body.push(*fps);
            push_str16(&mut body, title);
        }
        Msg::Frame { pts_ms, bytes } => {
            body.push(TYPE_FRAME);
            body.extend_from_slice(&pts_ms.to_le_bytes());
            body.extend_from_slice(bytes);
        }
        Msg::Status { state, position_ms, duration_ms, err } => {
            body.push(TYPE_STATUS);
            body.push(*state);
            body.extend_from_slice(&position_ms.to_le_bytes());
            body.extend_from_slice(&duration_ms.to_le_bytes());
            push_str16(&mut body, err);
        }
        Msg::Control(c) => {
            body.push(TYPE_CONTROL);
            push_control(&mut body, c);
        }
    }
    let mut out = Vec::with_capacity(4 + body.len());
    out.extend_from_slice(&(body.len() as u32).to_le_bytes());
    out.extend_from_slice(&body);
    out
}

/// Appends a `u16`-length-prefixed UTF-8 string.
fn push_str16(buf: &mut Vec<u8>, s: &str) {
    let bytes = s.as_bytes();
    buf.extend_from_slice(&(bytes.len() as u16).to_le_bytes());
    buf.extend_from_slice(bytes);
}

/// Appends a CONTROL op byte + its payload.
fn push_control(buf: &mut Vec<u8>, c: &Control) {
    match c {
        Control::Play => buf.push(0),
        Control::Pause => buf.push(1),
        Control::Stop => buf.push(2),
        Control::Seek(ms) => {
            buf.push(3);
            buf.extend_from_slice(&ms.to_le_bytes());
        }
        Control::Volume(v) => {
            buf.push(4);
            buf.push(*v);
        }
        Control::Load(url) => {
            buf.push(5);
            push_str16(buf, url);
        }
    }
}

/// Reads one framed message: 4-byte LE length, then exactly that many bytes.
pub async fn read_msg<R: AsyncRead + Unpin>(r: &mut R) -> io::Result<Msg> {
    let mut len_buf = [0u8; 4];
    r.read_exact(&mut len_buf).await?;
    let len = u32::from_le_bytes(len_buf) as usize;
    if len > MAX_MSG_LEN {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!("message length {len} exceeds MAX_MSG_LEN ({MAX_MSG_LEN})"),
        ));
    }
    let mut body = vec![0u8; len];
    r.read_exact(&mut body).await?;
    parse_body(&body)
}

/// Parses a message body (`type` byte + payload) per `docs/protocol.md`.
fn parse_body(body: &[u8]) -> io::Result<Msg> {
    let mut c = Cur::new(body);
    let ty = c.u8()?;
    match ty {
        TYPE_HELLO => Ok(Msg::Hello { proto: c.u16()?, flags: c.u8()? }),
        TYPE_STREAM_INFO => Ok(Msg::StreamInfo {
            w: c.u16()?,
            h: c.u16()?,
            pixfmt: c.u8()?,
            fps: c.u8()?,
            title: c.str16()?,
        }),
        TYPE_FRAME => Ok(Msg::Frame { pts_ms: c.u64()?, bytes: c.rest() }),
        TYPE_STATUS => Ok(Msg::Status {
            state: c.u8()?,
            position_ms: c.u64()?,
            duration_ms: c.u64()?,
            err: c.str16()?,
        }),
        TYPE_CONTROL => Ok(Msg::Control(parse_control(&mut c)?)),
        other => Err(io::Error::new(io::ErrorKind::InvalidData, format!("unknown msg type 0x{other:02x}"))),
    }
}

/// Parses a CONTROL op byte + its payload.
fn parse_control(c: &mut Cur) -> io::Result<Control> {
    match c.u8()? {
        0 => Ok(Control::Play),
        1 => Ok(Control::Pause),
        2 => Ok(Control::Stop),
        3 => Ok(Control::Seek(c.u64()?)),
        4 => Ok(Control::Volume(c.u8()?)),
        5 => Ok(Control::Load(c.str16()?)),
        other => Err(io::Error::new(io::ErrorKind::InvalidData, format!("unknown control op {other}"))),
    }
}

/// Cursor over a message body slice — small bounds-checked reads, no partial-read bugs.
struct Cur<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> Cur<'a> {
    fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    fn need(&self, n: usize) -> io::Result<()> {
        if self.pos + n > self.buf.len() {
            Err(io::Error::new(io::ErrorKind::UnexpectedEof, "message body too short"))
        } else {
            Ok(())
        }
    }

    fn u8(&mut self) -> io::Result<u8> {
        self.need(1)?;
        let v = self.buf[self.pos];
        self.pos += 1;
        Ok(v)
    }

    fn u16(&mut self) -> io::Result<u16> {
        self.need(2)?;
        let v = u16::from_le_bytes(self.buf[self.pos..self.pos + 2].try_into().unwrap());
        self.pos += 2;
        Ok(v)
    }

    fn u64(&mut self) -> io::Result<u64> {
        self.need(8)?;
        let v = u64::from_le_bytes(self.buf[self.pos..self.pos + 8].try_into().unwrap());
        self.pos += 8;
        Ok(v)
    }

    fn str16(&mut self) -> io::Result<String> {
        let len = self.u16()? as usize;
        self.need(len)?;
        let s = String::from_utf8(self.buf[self.pos..self.pos + len].to_vec())
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
        self.pos += len;
        Ok(s)
    }

    fn rest(&mut self) -> Vec<u8> {
        let v = self.buf[self.pos..].to_vec();
        self.pos = self.buf.len();
        v
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn roundtrip_stream_info() {
        let m = Msg::StreamInfo { w: 640, h: 360, pixfmt: 0, fps: 30, title: "clip".into() };
        let bytes = encode(&m);
        let mut cur = std::io::Cursor::new(bytes);
        let back = read_msg(&mut cur).await.unwrap();
        assert!(matches!(back, Msg::StreamInfo { w:640, h:360, pixfmt:0, fps:30, .. }));
    }

    #[tokio::test]
    async fn roundtrip_control_load() {
        let m = Msg::Control(Control::Load("http://x/y.mp4".into()));
        let mut cur = std::io::Cursor::new(encode(&m));
        match read_msg(&mut cur).await.unwrap() {
            Msg::Control(Control::Load(u)) => assert_eq!(u, "http://x/y.mp4"),
            _ => panic!("wrong variant"),
        }
    }

    #[tokio::test]
    async fn len_prefix_covers_type_plus_payload() {
        let bytes = encode(&Msg::Hello { proto: 1, flags: 0 });
        let len = u32::from_le_bytes(bytes[0..4].try_into().unwrap()) as usize;
        assert_eq!(len, bytes.len() - 4);
        assert_eq!(bytes[4], 0x01); // type HELLO
    }

    #[tokio::test]
    async fn roundtrip_hello() {
        let m = Msg::Hello { proto: 1, flags: 7 };
        let mut cur = std::io::Cursor::new(encode(&m));
        let back = read_msg(&mut cur).await.unwrap();
        assert_eq!(back, m);
    }

    #[tokio::test]
    async fn roundtrip_frame_preserves_pixel_bytes() {
        let pixels: Vec<u8> = (0u8..=255).collect();
        let m = Msg::Frame { pts_ms: 123_456, bytes: pixels.clone() };
        let mut cur = std::io::Cursor::new(encode(&m));
        match read_msg(&mut cur).await.unwrap() {
            Msg::Frame { pts_ms, bytes } => {
                assert_eq!(pts_ms, 123_456);
                assert_eq!(bytes.len(), pixels.len());
                assert_eq!(bytes, pixels);
            }
            _ => panic!("wrong variant"),
        }
    }

    #[tokio::test]
    async fn roundtrip_status() {
        let m = Msg::Status { state: 3, position_ms: 5_000, duration_ms: 60_000, err: "decode failed".into() };
        let mut cur = std::io::Cursor::new(encode(&m));
        let back = read_msg(&mut cur).await.unwrap();
        assert_eq!(back, m);
    }

    #[tokio::test]
    async fn roundtrip_control_play() {
        let mut cur = std::io::Cursor::new(encode(&Msg::Control(Control::Play)));
        assert_eq!(read_msg(&mut cur).await.unwrap(), Msg::Control(Control::Play));
    }

    #[tokio::test]
    async fn roundtrip_control_pause() {
        let mut cur = std::io::Cursor::new(encode(&Msg::Control(Control::Pause)));
        assert_eq!(read_msg(&mut cur).await.unwrap(), Msg::Control(Control::Pause));
    }

    #[tokio::test]
    async fn roundtrip_control_stop() {
        let mut cur = std::io::Cursor::new(encode(&Msg::Control(Control::Stop)));
        assert_eq!(read_msg(&mut cur).await.unwrap(), Msg::Control(Control::Stop));
    }

    #[tokio::test]
    async fn roundtrip_control_seek() {
        let mut cur = std::io::Cursor::new(encode(&Msg::Control(Control::Seek(42_000))));
        assert_eq!(read_msg(&mut cur).await.unwrap(), Msg::Control(Control::Seek(42_000)));
    }

    #[tokio::test]
    async fn roundtrip_control_volume() {
        let mut cur = std::io::Cursor::new(encode(&Msg::Control(Control::Volume(80))));
        assert_eq!(read_msg(&mut cur).await.unwrap(), Msg::Control(Control::Volume(80)));
    }

    #[tokio::test]
    async fn read_msg_rejects_oversized_length_prefix() {
        // A length prefix bigger than MAX_MSG_LEN, with no body bytes following. If `read_msg`
        // allocated `len` bytes and tried to read them before checking the cap, this would fail
        // with UnexpectedEof (or hang on a real socket) instead of the cap's InvalidData — so
        // asserting the InvalidData kind proves the cap is checked before the read/allocation.
        let huge_len = (MAX_MSG_LEN as u32).saturating_add(1);
        let bytes = huge_len.to_le_bytes().to_vec();
        let mut cur = std::io::Cursor::new(bytes);
        let err = read_msg(&mut cur).await.unwrap_err();
        assert_eq!(err.kind(), io::ErrorKind::InvalidData);
    }
}
