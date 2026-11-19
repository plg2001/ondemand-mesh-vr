# Sessione Quest: 20261119_121141_wifi

- Collegamento: **Wi-Fi** (server http://192.168.1.5:8000)
- Durata: 2.4 min (dalle 12:12:13, orologio del PC)
- Orologio del visore - orologio del PC: +7.10 s (misurato con adb)
- Visore: Quest 2
- Richieste all'Edge: 6; mesh dalla cache: 4

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | 80 | mediana 42 (31-183) |
| Caricamento del .glb, ms | 28 | mediana 28 (28-28) |
| Server, ms | 25678 | mediana 18727 (5502-29046) |
| Totale, ms | 25788 | mediana 18799 (5642-29118) |

Provenienza delle richieste secondo il server: 6 da 192.168.1.10 (Wi-Fi).

## Mesh generate e accuratezza (dal server)

Accuratezza rispetto alla mesh originale (nuvola densa di `build_scene.py`): F-score all'1% della diagonale, precisione (la mesh non inventa), richiamo (non dimentica), Chamfer-L2.

| Oggetto | Modello | Triangoli | Server [s] | F-score | Precisione | Richiamo | Chamfer-L2 |
|---|---|---|---|---|---|---|---|
| chair_01 | meshpad | 197 | 25.7 | 0.67 | 0.71 | 0.64 | 0.00096 |
| table_01 | meshpad | 116 | 19.3 | 0.49 | 0.62 | 0.41 | 0.00305 |
| bookcase_01 | meshpad | 180 | 29.0 | 0.41 | 0.52 | 0.34 | 0.00229 |
| box_01 | meshpad | 28 | 5.5 | 0.66 | 0.62 | 0.71 | 0.00386 |
| bench_01 | meshpad | 97 | 18.7 | 0.64 | 0.71 | 0.58 | 0.00303 |
| lamp_01 | meshpad | 46 | 8.4 | 0.46 | 0.89 | 0.31 | 0.00132 |

Il calcolo dell'accuratezza è compreso nel tempo del server: mediana 95 (83-110) ms per mesh (da togliere per la latenza della sola ricostruzione).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): chair_01 25.8 s, table_01 44.5 s, bookcase_01 46.5 s, box_01 5.6 s, bench_01 23.8 s, lamp_01 15.6 s
- Dalla cache: mediana 15.8 (15.6-15.8) ms

## Frame rate (VrApi, un campione al secondo)

- Obiettivo 72 fps; campioni: 123 s, di cui 1 di avvio dell'app (fino al primo secondo a regime), riportati a parte ed esclusi dal resto
- Avvio: fps 1; frame scartati 44
- fps a regime: mediana 72 (64-73); secondi sotto il 90% dell'obiettivo: 1 (1%)
- fps nei 2 s dopo la prima mesh dall'Edge: mediana 70 (69-72); dopo le altre dall'Edge: mediana 72 (72-73); dopo quelle dalla cache: mediana 72 (72-72); lontano dalle mesh: mediana 72 (64-73)
- Frame scartati (Stale) a regime: 45
- Tempo dell'app per frame (App): mediana 5.7 (1.6-6.6) ms
- Uso CPU / GPU: mediana 0.18 (0.11-0.24) / mediana 0.51 (0.37-0.69)
- Temperatura massima: 28 °C
