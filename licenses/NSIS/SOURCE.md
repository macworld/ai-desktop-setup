# NSIS runtime and corresponding source

The wrapper uses unmodified stock NSIS 3.12 Unicode exehead, System plugin and
LZMA decompressor. COPYING is retained in full, including the Common Public
License 1.0 and NSIS linking exception. These components keep their upstream
identity and licenses; the client MIT license does not relicense them.

The complete corresponding release source is available at:
https://downloads.sourceforge.net/project/nsis/NSIS%203/3.12/nsis-3.12-src.tar.bz2

SHA256: `f3ed7a8e4aa2cf4e8cf47d3b563a02559e0cb4934db2662b2f9661b824e2b186`.
The release process must retain and distribute this source archive as a companion
attachment with the installer, so recipients can obtain it from the same release.
No source modifications to these NSIS components are made by this project.
Source repository tag v312 resolves to
`e3f60402bcdf7be822d159b531c6e38ddf32de12`.

The selected standard Windows distribution and its digest are locked in
`build/toolchain.json`. Its checksum establishes the selected distribution bytes;
no independently reproduced compiler build or maintainer signature is claimed.
