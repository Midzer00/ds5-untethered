/* GitHub App authentication with WebCrypto only (no libraries):
 *
 *   privateKeyDer(pem)       -> PKCS#8 DER bytes. GitHub hands out the App's private key as a PKCS#1 PEM
 *                               ("BEGIN RSA PRIVATE KEY"); WebCrypto imports only PKCS#8, so PKCS#1 is wrapped here.
 *                               A PKCS#8 PEM ("BEGIN PRIVATE KEY") is used as given.
 *   pkcs1ToPkcs8(der)        -> the PKCS#8 PrivateKeyInfo around a PKCS#1 RSAPrivateKey
 *   appJwt(appId, pem, now?) -> an RS256 JWT for the App: iat 60 s in the past, exp 9 minutes ahead, iss = appId */

const RSA_ALGORITHM_IDENTIFIER = new Uint8Array([
  // SEQUENCE { OBJECT IDENTIFIER 1.2.840.113549.1.1.1 (rsaEncryption), NULL }
  0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00
]);

function derLength(n) {
  if (n < 0x80) return [n];
  const bytes = [];
  while (n > 0) { bytes.unshift(n & 0xff); n >>>= 8; }
  return [0x80 | bytes.length].concat(bytes);
}

function concat(parts) {
  const size = parts.reduce((s, p) => s + p.length, 0);
  const out = new Uint8Array(size);
  let at = 0;
  for (const p of parts) { out.set(p, at); at += p.length; }
  return out;
}

function tlv(tag, content) {
  return concat([new Uint8Array([tag].concat(derLength(content.length))), content]);
}

export function pkcs1ToPkcs8(pkcs1) {
  const version = new Uint8Array([0x02, 0x01, 0x00]);   // INTEGER 0
  return tlv(0x30, concat([version, RSA_ALGORITHM_IDENTIFIER, tlv(0x04, pkcs1)]));
}

// Reads one DER header at `at`: { tag, len, start (of the content) }.
function derHeader(bytes, at) {
  const tag = bytes[at];
  let len = bytes[at + 1];
  let start = at + 2;
  if (len & 0x80) {
    const n = len & 0x7f;
    if (n < 1 || n > 4) throw new Error('bad key');
    len = 0;
    for (let i = 0; i < n; i++) len = len * 256 + bytes[start + i];
    start += n;
  }
  if (start + len > bytes.length) throw new Error('bad key');
  return { tag, len, start };
}

// PKCS#1 RSAPrivateKey: SEQUENCE { INTEGER version, INTEGER modulus, ... }.
// PKCS#8 PrivateKeyInfo: SEQUENCE { INTEGER version, SEQUENCE algorithm, OCTET STRING key }.
function isPkcs1(der) {
  const outer = derHeader(der, 0);
  if (outer.tag !== 0x30) throw new Error('bad key');
  const version = derHeader(der, outer.start);
  if (version.tag !== 0x02) throw new Error('bad key');
  const next = derHeader(der, version.start + version.len);
  return next.tag === 0x02;
}

function base64ToBytes(b64) {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

export function privateKeyDer(pem) {
  // Tolerates a key pasted with literal "\n" escapes, CRLF or stray spaces.
  const text = String(pem || '').replace(/\\n/g, '\n').replace(/\r/g, '');
  if (/BEGIN (?:ENCRYPTED|EC|OPENSSH) PRIVATE KEY/.test(text)) throw new Error('unsupported key');
  const b64 = text.replace(/-----(?:BEGIN|END) [A-Z ]+-----/g, '').replace(/\s+/g, '');
  if (!b64 || !/^[A-Za-z0-9+/]+={0,2}$/.test(b64)) throw new Error('bad key');
  const der = base64ToBytes(b64);
  return isPkcs1(der) ? pkcs1ToPkcs8(der) : der;
}

function base64url(bytes) {
  let bin = '';
  for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function base64urlJson(obj) {
  return base64url(new TextEncoder().encode(JSON.stringify(obj)));
}

export async function importAppKey(pem) {
  return crypto.subtle.importKey('pkcs8', privateKeyDer(pem), { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' }, false, ['sign']);
}

export async function appJwt(appId, pem, nowSeconds) {
  const now = Math.floor(nowSeconds == null ? Date.now() / 1000 : nowSeconds);
  const id = String(appId).trim();
  const payload = {
    iat: now - 60,     // allows for clock drift, as GitHub's docs recommend
    exp: now + 540,    // GitHub refuses more than 10 minutes ahead
    iss: /^\d+$/.test(id) ? Number(id) : id
  };
  const input = base64urlJson({ alg: 'RS256', typ: 'JWT' }) + '.' + base64urlJson(payload);
  const key = await importAppKey(pem);
  const sig = new Uint8Array(await crypto.subtle.sign('RSASSA-PKCS1-v1_5', key, new TextEncoder().encode(input)));
  return input + '.' + base64url(sig);
}
