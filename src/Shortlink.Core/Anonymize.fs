namespace Shortlink.Core

open System
open System.Net

/// IP address anonymization for privacy-preserving visit tracking.
module Anonymize =

    /// Zero the host part of an address: last octet for IPv4, everything
    /// beyond the /48 prefix for IPv6. Returns None for unparseable input.
    let anonymizeIp (ip: string) : string option =
        match IPAddress.TryParse(ip) with
        | false, _ -> None
        | true, addr ->
            let bytes = addr.GetAddressBytes()
            if bytes.Length = 4 then
                bytes.[3] <- 0uy
                Some(IPAddress(bytes).ToString())
            else
                for i in 6 .. bytes.Length - 1 do
                    bytes.[i] <- 0uy
                Some(IPAddress(bytes).ToString())

    /// Check whether an IP falls in a CIDR range (used by redirect rules).
    let ipInCidr (cidr: string) (ip: string) : bool =
        match cidr.Split('/') with
        | [| network; prefixStr |] ->
            match IPAddress.TryParse(network), Int32.TryParse(prefixStr), IPAddress.TryParse(ip) with
            | (true, net), (true, prefix), (true, addr) ->
                let netBytes = net.GetAddressBytes()
                let addrBytes = addr.GetAddressBytes()
                if netBytes.Length <> addrBytes.Length || prefix < 0 || prefix > netBytes.Length * 8 then
                    false
                else
                    let fullBytes = prefix / 8
                    let remainder = prefix % 8
                    let fullMatch =
                        Seq.forall2 (=) (Seq.truncate fullBytes netBytes) (Seq.truncate fullBytes addrBytes)
                    let partialMatch =
                        remainder = 0
                        || fullBytes >= netBytes.Length
                        || (let mask = 0xFFuy <<< (8 - remainder)
                            netBytes.[fullBytes] &&& mask = (addrBytes.[fullBytes] &&& mask))
                    fullMatch && partialMatch
            | _ -> false
        | [| single |] ->
            // Bare address = exact match
            match IPAddress.TryParse(single), IPAddress.TryParse(ip) with
            | (true, a), (true, b) -> a.Equals b
            | _ -> false
        | _ -> false
