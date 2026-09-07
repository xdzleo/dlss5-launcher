"""Compatibility entry point for the Windows/WPF vector brand generator."""
import subprocess
from pathlib import Path

if __name__ == "__main__":
    subprocess.run([
        "powershell", "-NoProfile", "-STA", "-File",
        str(Path(__file__).with_name("gen_brand.ps1")),
    ], check=True)
