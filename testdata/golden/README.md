# Golden vectors

`vdl2_full_frame_iq_f32le.bin` is deterministic synthetic complex float32 IQ. It contains
the full 16-symbol VDL2 preamble, a valid scrambled header and a complete all-zero
RS(255,249) payload. The decoder must produce `hdlc_no_frame` with valid header and RS.
