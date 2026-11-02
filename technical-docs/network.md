# Network page and DNS providers

Status: implemented and tested with fakes and read-only checks. Not yet run on a test machine or virtual machine; setting DNS and DNS over HTTPS (DoH) is unverified on every Windows build. See [verification.md](verification.md) and the sources in [sources.md](sources.md).

## What the page shows

For each network adapter that is up and has an address: name, type, IPv4 and IPv6 addresses, the DNS servers Windows uses now, whether the servers are set on the adapter or come from the network (DHCP or router advertisements), and whether DoH is set for them. Hardware addresses are not read. Adapters that are virtual machine, VPN, tunnel or dial-up links are shown and marked "not changed by default".

## What it can change

A DNS provider, with DoH off, on with fall back to plain DNS, or on without fall back. Providers come from the reviewed file `definitions/network/DnsProviders.v1.json` that ships in the app: Cloudflare (standard, malware blocking, malware and adult content blocking), Google Public DNS, Quad9, OpenDNS (IPv4 only, no DoH) and AdGuard DNS. Two more choices: Automatic (servers from the network) and Custom (IP addresses only, up to four per kind, no DoH).

## Safety rules

- Windows functions: `GetInterfaceDnsSettings`, `SetInterfaceDnsSettings` and `FreeInterfaceDnsSettings` (iphlpapi, Windows 10 build 19041 and later; DoH settings need `DNS_INTERFACE_SETTINGS3`, build 19645 and later). One source file calls the setter. No command line tool, no script host, no process is started. The DNS client cache is not flushed because Microsoft does not document a function for it; a change applies to new lookups.
- Needs administrator rights to change; reading works without them.
- Every change shows the servers before and after for each chosen adapter and asks once.
- A line with the full previous settings of every adapter (servers, DoH settings, and whether they came from the network) is written to the change history and flushed to disk before the first change.
- A setting that someone else changed after the review is not overwritten. Each adapter is read back after the change.
- Undo puts back the exact previous settings, including "from the network", for each adapter that still has what the change wrote.
- Virtual and VPN adapters are only changed when the user adds them on purpose after an extra confirmation.
- After a change, one lookup of a Windows connectivity name tests name resolution. If it fails, the page says so and offers Undo. Winora does not undo by itself.

## Limits

- IPv6 servers are set only on adapters that have an IPv6 address. A provider without IPv6 servers leaves IPv6 DNS as it is, and the review says so.
- The page does not read the Group Policy setting for DNS over HTTPS or the system list of known DoH servers. It cannot see automatic DoH for servers that come from the network.
- A lookup answer that Windows already holds can hide a failure of a new server for a short time.
