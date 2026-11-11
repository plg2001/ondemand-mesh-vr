# Sessione Quest: 20261111_115405_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.27:8000)
- Durata: 0.7 min (dalle 11:54:10, orologio del PC)
- Orologio del visore - orologio del PC: +0.06 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 7

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 22 | mediana 29 (25-44) |
| Caricamento del .glb, ms | 59 | mediana 28 (28-28) |
| Server, ms | 2013 | mediana 2007 (2005-2018) |
| Totale, ms | 2096 | mediana 2064 (2062-2092) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): puppet_01 2.1 s, cat_01 5.3 s, box_01 4.1 s, bunny_01 5.7 s, wand_01 8.2 s, sphere_01 10.0 s
- Dalla cache: mediana 15.4 (14.9-17.9) ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 36 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 75; frame scartati 58
- fps a regime: mediana 72 (50-73); secondi sotto il 90% dell'obiettivo: 2 (6%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 61 (50-72); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: mediana 72 (72-73); lontano dalle mesh: mediana 72 (62-72)
- Frame scartati (Stale) a regime: 41
- Tempo dell'app per frame (App): mediana 6.5 (1.6-8.2) ms
- Uso CPU / GPU: mediana 0.14 (0.10-0.23) / mediana 0.56 (0.44-0.73)
- Temperatura massima: 26 °C
