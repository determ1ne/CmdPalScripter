function nslookup(host) {
    const target = String(host || '').trim();

    if (!target) {
        throw new Error('Usage: nslookup <host>');
    }

    if (!/^[A-Za-z0-9._:-]+$/.test(target)) {
        throw new Error('Invalid nslookup target. Use a host name, IPv4 address, or IPv6 address without spaces.');
    }

    return $.exec('nslookup.exe', [target]);
}
