"""This PC's addresses on the local network: what a headset on the same Wi-Fi must use to reach the edge server."""
from __future__ import annotations

import socket


def lan_ipv4_addresses() -> list[str]:
    """IPv4 addresses of this PC, the one of the default route first; loopback and link-local excluded."""
    found: list[str] = []
    try:
        # connect() on a UDP socket sends nothing: it only asks the OS which interface would be used
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("8.8.8.8", 80))
            found.append(s.getsockname()[0])
    except OSError:  # no default route (offline): fall back to the host name
        pass
    try:
        for ip in socket.gethostbyname_ex(socket.gethostname())[2]:
            if ip not in found:
                found.append(ip)
    except OSError:
        pass
    return [ip for ip in found if not ip.startswith(("127.", "169.254.", "0."))]
