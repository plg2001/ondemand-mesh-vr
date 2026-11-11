# Sessione Quest: 20261111_115613_usb

- Collegamento: **cavo USB** (server http://127.0.0.1:8000)
- Durata: 0.7 min (dalle 11:56:17, orologio del PC)
- Orologio del visore - orologio del PC: +0.06 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 8

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 20 | mediana 16 (14-27) |
| Caricamento del .glb, ms | 52 | mediana 28 (28-28) |
| Server, ms | 2013 | mediana 2006 (2006-2018) |
| Totale, ms | 2089 | mediana 2062 (2048-2063) |

Provenienza delle richieste secondo il server: 6 da 127.0.0.1 (cavo USB o editor).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): bunny_01 2.1 s, puppet_01 2.1 s, cat_01 5.3 s, box_01 3.6 s, wand_01 5.7 s, sphere_01 7.4 s
- Dalla cache: mediana 15.4 (2.1-18.0) ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 38 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 73; frame scartati 55
- fps a regime: mediana 72 (54-73); secondi sotto il 90% dell'obiettivo: 1 (3%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 62 (54-70); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: mediana 72 (72-73); lontano dalle mesh: mediana 72 (67-72)
- Frame scartati (Stale) a regime: 38
- Tempo dell'app per frame (App): mediana 6.8 (1.2-14.5) ms
- Uso CPU / GPU: mediana 0.15 (0.10-0.21) / mediana 0.57 (0.27-0.63)
- Temperatura massima: 28 °C
