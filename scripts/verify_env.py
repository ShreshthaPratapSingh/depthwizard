#!/usr/bin/env python3
"""Verify the DepthWizard GPU environment (RTX 5070 / Blackwell, sm_120).

Prints:
  * torch version
  * torch.cuda.is_available()
  * the detected device compute capability
and then runs one dummy fp16 conv on the GPU as an end-to-end smoke test.

Exit code is 0 on success, 1 if CUDA is unavailable or the conv fails.
"""
import sys


def main() -> int:
    import torch

    print(f"torch version           : {torch.__version__}")
    cuda_ok = torch.cuda.is_available()
    print(f"torch.cuda.is_available : {cuda_ok}")
    print(f"compiled CUDA version   : {torch.version.cuda}")
    print(f"wheel arch list         : {torch.cuda.get_arch_list()}")

    if not cuda_ok:
        print(
            "\nERROR: CUDA is not available. Check the NVIDIA driver and confirm "
            "the cu128 wheel is installed — cu121 wheels do not support sm_120."
        )
        return 1

    dev = torch.device("cuda:0")
    name = torch.cuda.get_device_name(dev)
    major, minor = torch.cuda.get_device_capability(dev)
    print(f"device                  : {name}")
    print(f"device capability       : sm_{major}{minor}  (compute {major}.{minor})")

    if (major, minor) < (12, 0):
        print(
            "WARNING: capability below sm_120 — this does not look like a "
            "Blackwell GPU. Verify you are running on the RTX 5070."
        )
    if f"sm_{major}{minor}" not in torch.cuda.get_arch_list():
        print(
            "WARNING: the installed torch wheel does not advertise this device's "
            "arch. You likely have a non-cu128 wheel; fp16 conv may fail below."
        )

    # Dummy fp16 conv smoke test on the GPU.
    try:
        x = torch.randn(1, 3, 64, 64, device=dev, dtype=torch.float16)
        conv = torch.nn.Conv2d(3, 8, kernel_size=3, padding=1).to(dev).half()
        with torch.no_grad():
            y = conv(x)
        torch.cuda.synchronize()
        print(
            f"fp16 conv on GPU        : OK  -> output {tuple(y.shape)} "
            f"({y.dtype}, {y.device})"
        )
    except Exception as exc:  # noqa: BLE001 - report any GPU failure verbatim
        print(f"fp16 conv on GPU        : FAILED -> {exc!r}")
        return 1

    print("\nEnvironment looks good.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
