# Sessione Quest: 20261111_112533

- Collegamento: **non registrato**
- Orologio del visore - orologio del PC: +14.04 s (stimato abbinando le richieste al log del server)
- Richieste all'Edge: 10; mesh dalla cache: 29

## Tempi per richiesta (misurati dall'app)

Prima richiesta di ogni avvio dell'app (pausa > 60 s) separata dalle altre.

| Misura | Prima richiesta | Richieste successive |
|---|---|---|
| Rete (andata e ritorno - server), ms | mediana 119 (73-128) | mediana 57 (22-145) |
| Caricamento del .glb, ms | mediana 62 (56-67) | mediana 28 (28-28) |
| Server, ms | mediana 2016 (2007-2115) | mediana 2019 (2010-2024) |
| Totale, ms | mediana 2204 (2145-2304) | mediana 2106 (2063-2192) |

Provenienza delle richieste secondo il server: 10 con indirizzo non registrato (server di una versione precedente).

## Dall'ingresso in vista alla mesh

- Dall'Edge (in ordine): puppet_01 2.2 s, sphere_01 3.2 s, puppet_01 2.1 s, sphere_01 3.4 s, puppet_01 2.5 s, box_01 3.7 s, cat_01 4.3 s, bunny_01 9.2 s, wand_01 9.0 s, sphere_01 12.0 s
- Dalla cache: mediana 15.5 (1.4-21.4) ms

## Frame rate (VrApi, un campione al secondo)

Nessun dato: gli fps si registrano solo con `quest.py session`.
