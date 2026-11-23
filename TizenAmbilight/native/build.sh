#!/bin/sh
# Rebuild res/libtzcapturec.so. TV userland is 32-bit ARM EABI5 soft-float (readelf -h of any TV library),
# so the stub must match: a hard-float object is refused by the TV's dynamic linker.
set -eu
cd "$(dirname "$0")"
clang --target=armv7a-linux-gnueabi -mfloat-abi=soft -mthumb -O2 -fPIC -shared -nostdlib \
  -fuse-ld=lld -Wl,--hash-style=both -Wl,-soname,libtzcapturec.so -Wl,--build-id=none \
  -o ../res/libtzcapturec.so tzcapturec_stub.c
readelf -h ../res/libtzcapturec.so | grep -q 'soft-float ABI'
echo "ok: ../res/libtzcapturec.so"
