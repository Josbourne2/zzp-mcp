# Flextender

De bron `flextender` gebruikt de publieke website. De gebruiker heeft bevestigd dat login niet nodig is wanneer volledige teksten en locaties openbaar opgehaald kunnen worden. Accountgebonden aanbevelingen van `/supplier/jobs/recommended` worden niet nagebootst.

## Websitecontract

- GET `https://www.flextender.nl/opdrachten/`: lees het actuele `form.pc-search` binnen `.widget_flextender_search_jobs`.
- POST de succesvolle formulierinputs naar de waargenomen route `/wp-admin/admin-ajax.php`, met action `kbs_flx_searchjobs`, actuele opaque `kbs_flx_widget_config` en zoekterm `kbs_flx_joblsrc_freetext`. Ongeselecteerde filters worden niet meegestuurd. De opaque configuratie wordt niet opgeslagen of gelogd.
- `resultHtml` bevat alle `.css-foundjob`-kaarten, inclusief verborgen client-side vervolgpagina's. De live proef op 6 november 2026 gaf 217 opdrachten; dit aantal kan veranderen.
- `aanvraagnr` is de bron-ID. GET `/opdracht/?aanvraagnr={id}` levert de volledige tekst. De slash voorkomt een waargenomen 301. Bewaar geen `resultatenlijst`-parameter.
- Titel, aanvraagnummer en `.flx-job-mce-container` moeten aanwezig zijn en overeenkomen. Een loginpagina, blokkade, ontbrekende markup of HTTP-fout wordt geen leeg succes. Alleen de waargenomen melding `Er zijn geen opdrachten gevonden.` bevestigt nul resultaten.

## Gebruik en beperkingen

Gebruik `collect_jobs(sessionId, source="flextender", batchSize=10)` en hervat met dezelfde `collectionId` tot `complete=true`. De collector legt eerst de volledige ongefilterde lijst vast en haalt alleen ontbrekende details op. Voortgang en detailcache overleven een herstart. Scans verwijderen niets uit de gedeelde database. Lees volledige teksten met `get_job` vóór matching.

`search_jobs(source="flextender", query="…")` gebruikt de publieke bronzoekfunctie en deelt de lijst lokaal op volgens `pageSize`/`nextCursor`. Iedere geleverde treffer bevat volledige details. Volledige-tekstdekking van de bronzoekfunctie is niet vastgesteld; verzamel breed wanneer tekstdekking belangrijk is. Er is nog geen lokale tekstzoektool. Vervolgaanroepen lezen de actuele bronlijst opnieuw; veranderingen kunnen verschuivingen veroorzaken.

`locations` bevat het bronveld **Regio**: een provincie, geen exacte standplaats. Specifiekere locatie-informatie blijft behouden in de volledige tekst. `location` wordt expliciet afgewezen omdat plaats-/kilometersemantiek niet is vastgesteld. Een gezamenlijke zoekactie op Utrecht kan dus voor Flextender `unsupported_filter` opleveren; zoek deze bron apart zonder locatie en beoordeel de teksten. Een losse `radiusKm` zonder `location` wordt niet toegepast.

Alle samenvattingsvelden, waaronder een eventueel DAS-veld, worden in de volledige tekst bewaard. Eisen, tarief en contractvoorwaarden zijn leesbaar maar niet allemaal apart gestructureerd. Een DAS-label bewijst geen gedeelde opdrachtidentiteit tussen bronnen.

## Verificatie

Offline fixtures bevatten twee publieke kaarten, inclusief de laatste verborgen pagina, en de inhoud van aanvraag 33782. Tests controleren onder andere volledige tekst, identiteit, provincie, Nederlandse deadline, actuele formulierwaarden, cachehergebruik en fouten zonder gefingeerde nulresultaten.

Live MCP-proef met tijdelijke database:

```powershell
dotnet run --project tools/ZzpMcp.Smoke -c Release --no-build -- --flextender-live E:/source/zzp-mcp/src/ZzpMcp/bin/Release/net10.0/ZzpMcp.dll
```

Geslaagd: zoeken met volledige tekst en provincie, verschillende vervolgpagina's, brede lijstverzameling, cachehergebruik in een nieuw serverproces en hervatting van dezelfde verzameling. Deze proef leest enkele details, niet alle detailteksten. Gebruik geen productiedatabase via `ZZPMCP_DATABASE_PATH`: standaard smokecontroles voegen synthetische testgegevens toe.

## Kandidaatbron Werken in Gelderland

De officiële [inhuuruitleg](https://www.werkeningelderland.nl/uitleg-inhuur/) vermeldt de overstap naar het DAS van Flextender op 3 april 2025. Reageren verloopt via Flextender; opdrachten blijven zichtbaar op [het eigen inhuuroverzicht](https://www.werkeningelderland.nl/inhuur/). Dat overzicht had bij inspectie 76 opdrachten en 8 pagina's. Dit is een kandidaat voor later brononderzoek en overlapcontrole; er is geen aparte adapter.

`opdrachtoverheid.nl/?provincie=Gelderland` was via de webtool niet bereikbaar. De rol als primaire bron of aggregator is niet vastgesteld. Advertentietrackingparameters zijn niet nodig voor het onderzoek.
