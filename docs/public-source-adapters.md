# Openbare bronadapters

Op 6 november 2026 zijn **Freep, MiPublic, Magnit Source, LibLab en Werken in Gelderland** toegevoegd; op 7 november ook **StarApple**. Samen met Harvey Nash, Vertage en Flextender heeft de MCP-server negen adapters. De zes openbare adapters gebruiken uitsluitend anonieme HTTP; geen browser, account of gekopieerde sessiegegevens.

## Bronnen en identiteit

| Source | Inventaris en bron-ID | Scope |
| --- | --- | --- |
| `freep` | GET `https://www.freep.nl/`, opdrachtenarray uit `__NUXT_DATA__`; positief numeriek `id` | Alle openbare lijstidentiteiten. Geen hardgecodeerde Nuxt-indexen. Slug wordt bij de bron-ID gehouden en vanuit opgeslagen lijstmetadata hersteld. |
| `mipublic` | GET `https://mipublic.nl/interim-opdrachten`, volledige JSON-LD ItemList; slug van `/vacature/{slug}` | Alle vakgebieden. Aantal URL's moet overeenkomen met `numberOfItems`; geen onvolledige lijst als compleet presenteren. |
| `magnit` | Anonieme POST `/api/jobsearch` op `app-openmarketgateway-prod.azurewebsites.net`; UUID | Wereldwijd publiek aanbod, statuscode `4` zoals waargenomen bij het websitefilter Open. Geen land- of klantportaaldekking geclaimd. |
| `liblab` | GET `https://liblab.nl/onze-opdrachten/` en `?_paged=N`; slug van `/opdrachten/{slug}/` | Native bronpagina's met FacetWP-paginabewijs. `collect_jobs` legt één native pagina per aanroep vast. |
| `werken-in-gelderland` | GET `/inhuur/` en `/inhuur/page/N/`; slug van `/opdracht/{slug}/` | Alle gevonden inhuurpagina's. Een link kan om de volledige kaart staan. Reageren verwijst naar Flextender; bronidentiteiten worden niet samengevoegd. |
| `starapple` | GET `https://www.starapple.nl/vacatures/` en `/vacatures/page/N/`; slug van `/vacatures/{slug}/` | Publieke vacatures inclusief herkenbare open sollicitaties. Eén native pagina van maximaal twintig vermeldingen per collectoraanroep; geen bewezen dekking van StarApple-aanbod op Striive/Vertage. |

Freep en MiPublic hebben afzonderlijke volledige detailpagina's. Magnit gebruikt de anonieme GET `/api/jobsearch/{uuid}/details`. LibLab en Gelderland gebruiken hun publieke detail-HTML. Canonieke URL's en gevraagde identiteiten worden gecontroleerd; een doorgestuurde loginpagina of andere vacature is geen geldig detail.

Gestructureerde beschrijvingen worden alleen gebruikt waar ze in de leesproef de opdrachtinhoud, eisen en wensen bevatten. Bij Freep wordt de zichtbare beschrijving gebruikt. Bij LibLab wordt de zichtbare opdrachtinhoud inclusief het afzonderlijke contractdisclaimerblok gelezen, omdat `CONTRACTOR` in JSON-LD kan botsen met een expliciete uitsluiting van zzp. Gelderland behoudt ook de bronverwijzing naar Flextender. Ruwe payloads, logo's en externe aanbiedingslinks worden niet als vacaturedata opgeslagen.

## Verzamelen en zoeken

Begin met `start_session`. Verzamel vervolgens per bron:

```text
collect_jobs(sessionId=..., source="freep", batchSize=10)
collect_jobs(sessionId=..., source="mipublic", batchSize=10)
collect_jobs(sessionId=..., source="magnit", batchSize=10)
collect_jobs(sessionId=..., source="liblab", batchSize=10)
collect_jobs(sessionId=..., source="werken-in-gelderland", batchSize=10)
collect_jobs(sessionId=..., source="starapple", batchSize=10)
```

Hervat iedere collectie met haar eigen `collectionId` tot `complete=true`. Alleen ontbrekende volledige teksten worden opgehaald. Bestaande details worden hergebruikt met hun oorspronkelijke `fetchedAt`; de cache is geen actuele broncontrole. Een bron-/zoekscan verwijdert nooit gedeelde vacatures, detailversies of sessiehistorie. Schemawijziging of datamigratie was hiervoor niet nodig.

`search_jobs` haalt een begrensde pagina op en verrijkt die eerst met de volledige teksten. Daarna filtert de service `query` **lokaal en letterlijk** op titel, opdrachtgever en die volledige teksten. `appliedSourceFilters` claimt geen native bronzoekterm; `appliedLocalFilters` noemt de werkelijke scope. Bij een ingevulde zoekterm is `localMatchTotal=null`, omdat de rest van de bron nog niet op tekst is onderzocht. `sourceTotal` blijft het gerapporteerde onbewerkte brontotaal, voor zover vastgesteld.

**Volg `nextCursor` ook wanneer een pagina nul passende treffers geeft.** De cursor telt alle verwerkte bronrecords, niet alleen matches. Voor onderzoek van het hele corpus heeft brede verzameling de voorkeur. Dit voegt geen SQLite-FTS of algemene lokale cachezoektool toe.

De nieuwe adapters wijzen `location` en bronspecifieke `categories` af met `unsupported_filter`. Er is geen geverifieerde kilometersemantiek. Een gezamenlijke zoekvraag met locatie kan daardoor gedeeltelijke resultaten en expliciete bronfouten teruggeven. Verzamel deze bronnen dan afzonderlijk zonder locatie en beoordeel de bronvermeldingen. Ontbrekende plaats/opdrachtgever wordt niet uit de sitenaam afgeleid. Zo leverde de Gelderland-proef een lege locatie op; dat wordt niet automatisch Gelderland of Arnhem.

## Grenzen en foutgedrag

- Iedere HTTP-response is begrensd op 10 MiB en 25 seconden; geen automatische retries of redirects naar login. HTTP 401/403, 429, onverwachte inhoud en afgebroken responses zijn fouten, geen nulresultaten.
- De openbare inventaris is begrensd op 1000 identiteiten. Een groter brontotaal geeft een expliciete limietfout. Zoekpaginering behoudt de bestaande generieke limiet van 1000 bronrecords.
- Magnit gebruikt de ontvangen `continuationToken` alleen voor het volgende anonieme request. Herhaalde tokens/ID's, ontbrekende records en veranderende totalen stoppen de inventarisatie. Deze tokens worden niet opgeslagen of gelogd.
- LibLab bleek verschillende gecachete totalen per pagina te publiceren. Daarom worden native pagina's afzonderlijk vastgelegd, met hun eigen bronstatus. `complete` geldt voor de waargenomen leden en hun detailachterstand; het bewijst geen atomische momentopname van alle actuele opdrachten. Overlap wordt door de bestaande collector als onvolledig gemeld; er wordt niets gewist.
- Kalenderdatums zonder tijdzone krijgen geen verzonnen UTC-tijdstip; de bronwaarde blijft als tekst beschikbaar. Nulbedragen zijn geen aangeboden nultarief. Contractcodes en algemene labels bewijzen geen zzp-/payrolltoestemming.

## Verificatie

128 offline tests geslaagd, waaronder 26 nieuwe gevallen voor deze adapters en hun gedeelde gedrag. Gecontroleerd zijn onder andere dynamische Nuxt-verwijzingen, volledige ItemList-dekking, Magnit-cursors en herhaling, verkeerde detailidentiteit, expliciete HTTP-fouten zonder retries, native paginering, cachehergebruik en een lege lokale matchpagina met geldige vervolgcursor.

Live leesproeven met tijdelijke databases, via de uiteindelijke adapters en JobService:

| Source | Waargenomen lijstomvang | Volledige tekst gelezen | Hervatten en cache in nieuwe service/store-instantie |
| --- | --- | --- | --- |
| `freep` | 454 | Ja | Geslaagd |
| `mipublic` | 723 | Ja | Geslaagd |
| `magnit` | 128 wereldwijd | Ja | Geslaagd |
| `liblab` | 12 identiteiten op eerste native pagina | Ja | Geslaagd; tweede bronpagina en tweede detail verwerkt |
| `werken-in-gelderland` | 82 | Ja | Geslaagd |

Dit zijn momentopnamen. De proeven hebben enkele details per bron gelezen, geen volledige import van alle detailteksten. Ze gebruikten geen productiedatabase. De broninventarisatie en de latere live proeven kunnen daarom andere aantallen noemen.

De MCP-smokecontrole ontdekt alle negen bronnen en leest per openbare adapter een vooraf opgeslagen detail via `get_job`. Zij controleert ook dat een kilometerzoekvraag een expliciete fout geeft. De standaard smokecontrole gebruikt een tijdelijke database en heeft geen netwerk nodig; stel daarvoor nooit een productiedatabase in.

## StarApple: tekst, contractvormen en verificatie

De adapter leest de zichtbare `.vacancy-hero` en alle `.content-section .prose`-blokken: introductie, organisatie, functie, functie-eisen en aanbod, inclusief het aanbod in de zijbalk. De site heeft geen JobPosting JSON-LD; WebPage-datums worden niet als vacaturedatums gebruikt. Sollicitatieformulier, recruiterkaart, scripts en styles worden niet als vacaturetekst opgenomen. De plaats naast de titel wordt als locatie bewaard; het template noemt dit intern Company Name, maar dat bewijst geen werkgever.

Salarissen houden de oorspronkelijke tekst en aantoonbare periode. Een expliciet bruto maandsalaris wordt `kind=salary`, `unit=month`; een bedrag in de kop zonder periode krijgt geen verzonnen tijdseenheid. Een los euroteken zonder bedrag levert geen vergoeding op. Open sollicitaties krijgen de waarschuwing dat ze geen concrete vacature zijn; een ontbrekende functieomschrijving bij een concrete vacature is een fout.

ZZP en payroll blijven onafhankelijke beoordelingen op expliciet tekstbewijs. Reguliere arbeidsvoorwaarden, het StarApple-merk of hun algemene payrollaanbod bewijzen geen toestemming bij een specifieke opdracht. Als de gebruiker payroll accepteert, moet de agent ook opdrachten met ZZP uitgesloten en payroll toegestaan meenemen. De gedeelde contractparser herkent `Geen ZZP toegestaan` nu zonder een vals conflict met het woord toegestaan; werkelijk tegenstrijdige uitspraken blijven een conflict.

Paginering controleert de canonical tegen de gevraagde pagina, de kaarten, unieke identiteiten en de native paginalinks. Maximaal vijftig pagina's van twintig vermeldingen. Er wordt geen brontotaal uit het laatste paginanummer berekend: `sourceTotal=null`. Veranderend aanbod kan overlap of omissies geven; dit is geen snapshot. De gedeelde collector meldt herhaling als onvolledig en verwijdert niets.

Verificatie op 7 november 2026: **142 offline tests geslaagd**, inclusief twaalf StarApple-gevallen en twee aanvullende contractgevallen. De live MCP-proef las **343 verschillende vermeldingen op achttien native pagina's** en controleerde volledige detailtekst, maandsalaris, zoekcursor, verzamelvoortgang en detailcache na herstart van het serverproces. Alleen enkele details zijn opgehaald, geen volledige detailimport. Hiervoor is een tijdelijke database gebruikt. De 344 geschatte vermeldingen uit de eerdere inventarisatie zijn een andere momentopname.

Herhaal de live MCP-proef met de gebouwde server-DLL:

```powershell
dotnet run --project tools/ZzpMcp.Smoke -- --starapple-live E:\source\zzp-mcp\src\ZzpMcp\bin\Debug\net10.0\ZzpMcp.dll
```

Voorbeelden: `search_jobs(source="starapple", query="C#", pageSize=5)` en `get_job(source="starapple", sourceId="senior-net-developer-18")`. Geef de onderzoekssessie expliciet mee en vervolg alle cursors, ook bij nul matches.

Debug- en Release-binaries moeten na een update door de MCP-host opnieuw worden gestart en ontdekt. Een bestaand serverproces blijft zijn geladen oude code gebruiken.
