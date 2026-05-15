# R2 — Protocole Philips Hue Entertainment v2 (2026-05-15)

Recherche menée en sous-agent, archivée pour J2 (driver Hue).
Implémentation prévue en direct, sans NuGet tiers.

## Verdict synthétique

**Faisable à 80 %, le DTLS-PSK est le seul vrai blocker.**

Le pipeline Hue Entertainment v2 tient en cinq étapes :
1. Découverte du bridge (mDNS `_hue._tcp.local.`, fallback cloud
   `discovery.meethue.com`).
2. Pairing par bouton link (REST CLIP v1, `POST /api` avec
   `generateclientkey=true`).
3. Lecture des Entertainment Configurations (REST CLIP v2,
   `GET /clip/v2/resource/entertainment_configuration`).
4. Activation streaming (REST CLIP v2, `PUT .../{id}` avec
   `{"action":"start"}`, port DTLS 2100 ouvert).
5. Émission de paquets « HueStream v2 » à 50 Hz sur UDP via tunnel
   DTLS-PSK.

Les étapes 1, 2, 3, 4 et 6 (sérialisation paquet) sont triviales en
.NET 10 pur (`HttpClient`, `System.Text.Json`, `UdpClient`,
`BinaryPrimitives`). mDNS demande soit P/Invoke `windns` soit ~200
lignes de DNS-over-UDP custom.

## Le blocker — DTLS-PSK

**.NET 10 `SslStream` ne supporte ni DTLS ni les cipher suites PSK.**
Schannel non plus en mode PSK par défaut. Cipher requis :
`TLS_PSK_WITH_AES_128_GCM_SHA256` (0x00A8), DTLS 1.2 uniquement
(pas 1.3).

**Voie retenue (recommandée par la recherche) :** P/Invoke vers une
DLL OpenSSL ou mbedTLS embarquée dans le bundle natif Deckle —
cohérent avec le pattern déjà en place pour `whisper.cpp` /
`libwhisper.dll`. APIs nécessaires côté OpenSSL :
`SSL_CTX_set_psk_client_callback`, `BIO_new_dgram`, `SSL_connect`.
~400-600 lignes de bindings C# attendues.

**Alternative à considérer pour J2 initial :** démarrer par la voie
REST CLIP v2 simple (`PUT /clip/v2/resource/grouped_light/{id}`)
plafonnée à ~10-20 Hz mais sans crypto exotique. Permet de valider
discovery + pairing + control avant d'investir dans DTLS. Le pipeline
J3 (end-to-end minimal) pourrait tourner sur cette voie REST puis
migrer en Entertainment v2 quand le tunnel DTLS est prêt.

## Détails clés du protocole

**Pairing.** Endpoint CLIP v1 (toujours actif sur bridge v2),
`POST https://<bridge-ip>/api` avec
`{"devicetype":"deckle#<hostname>","generateclientkey":true}`.
Avant pression du bouton link : `error 101 "link button not pressed"`.
Après : retourne `username` (32 hex, sert d'identité PSK en bytes
ASCII pour DTLS et de `hue-application-key` pour le REST) et
`clientkey` (32 hex = **16 octets binaires** décodés = PSK pour DTLS).

**Format paquet HueStream v2** (52 octets de header + N records de 7
octets) :
- Bytes 0-8 : magic ASCII `"HueStream"`.
- Bytes 9-10 : version `0x02 0x00`.
- Byte 11 : sequence number.
- Bytes 12-13 : reserved.
- Byte 14 : color mode (`0x00` = RGB 16-bit, `0x01` = xy+brightness).
- Byte 15 : reserved.
- Bytes 16-51 : UUID ASCII de l'Entertainment Configuration (36
  chars avec tirets).
- Puis records de 7 octets : channel_id (1 byte) + 3 valeurs 16-bit
  big-endian.

**Cadence.** 50 Hz max recommandé. Bridge time-out le tunnel après
**~10 s d'inactivité** — keep-alive par envoi de la dernière frame
à 1 Hz minimum suffit. Shutdown propre via `PUT {"action":"stop"}`
avant fermeture socket (sinon zone verrouillée 10 s).

**Limite channels.** Variable selon zone/bridge (historiquement 10,
jusqu'à 20 sur récents) — lire `channels.length` de la config, pas
de constante hardcodée.

## Sources

- [New Hue API — Philips Hue Developer Program](https://developers.meethue.com/new-hue-api/)
- [Hue Entertainment Blog](https://developers.meethue.com/entertainment-blog)
- [Get Started — Philips Hue Developer Program](https://developers.meethue.com/develop/get-started-2/)
- [Philips Hue Entertainment API — IoTech Blog](https://iotech.blog/posts/philips-hue-entertainment-api/) — packet format détaillé
- [Q42.HueApi EntertainmentApi.md](https://github.com/michielpost/Q42.HueApi/blob/master/EntertainmentApi.md)
- [Q42.HueApi StreamingHueClient.cs](https://github.com/michielpost/Q42.HueApi/blob/master/src/HueApi.Entertainment/StreamingHueClient.cs) — réf DTLS BouncyCastle
- [.NET 10 Networking Improvements](https://devblogs.microsoft.com/dotnet/dotnet-10-networking-improvements/)
- [How to support TLS PSK in C# — André Snede](https://snede.net/how-to-support-tls-psk-in-c-pre-shared-key/)
- [Hueblog — 10-lamp limit](https://hueblog.com/2022/04/05/hue-entertainment-how-the-10-lamp-limit-could-be-bypassed/)
- [Talking to Philips Hue lights — rjbs.cloud](https://rjbs.cloud/blog/2023/01/talking-to-philips-hue-lights-3/) — mDNS pratique
