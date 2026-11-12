# Sessione Quest: 20261112_104605_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.27:8000)
- Durata: 0.6 min (dalle 10:46:15, orologio del PC)
- Orologio del visore - orologio del PC: +1.14 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 2

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 28 | mediana 42 (29-125) |
| Caricamento del .glb, ms | 28 | mediana 28 (28-28) |
| Server, ms | 2014 | mediana 2006 (2005-2018) |
| Totale, ms | 2071 | mediana 2089 (2063-2162) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.28 (Wi-Fi).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): bunny_01 2.1 s, sphere_01 2.1 s, puppet_01 4.1 s, box_01 6.2 s, wand_01 7.9 s, cat_01 9.6 s
- Dalla cache: mediana 15.2 (15.0-15.4) ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 29 s, di cui 2 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1, 73; frame scartati 155
- fps a regime: mediana 72 (51-72); secondi sotto il 90% dell'obiettivo: 2 (7%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 62 (51-72); dopo le altre dall'Edge: mediana 72 (72-72); dopo quelle dalla cache: mediana 72 (72-72); lontano dalle mesh: mediana 72 (59-72)
- Frame scartati (Stale) a regime: 44
- Tempo dell'app per frame (App): mediana 7.2 (1.6-21.4) ms
- Uso CPU / GPU: mediana 0.16 (0.12-0.38) / mediana 0.62 (0.44-0.72)
- Temperatura massima: 23 °C
