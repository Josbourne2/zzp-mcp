# Verificatie en oplevering

**Actuele uitbreiding:** standaard zoeken over Harvey Nash én Vertage, met loginactie en hervatten, is live geverifieerd; 59 offline tests slagen. Zie [zoeken op alle bronnen](multi-source-search.md) voor het actuele gedrag en de laatste verificatie. Het onderstaande verslag beschrijft de oorspronkelijke eerste PoC met 49 tests en de toen nog afzonderlijke browserproef.

Uitgevoerd op 5 november 2026, Windows, .NET SDK 10.0.401 / runtime 10.0.13, MCP C# SDK 2.3.1. Dit verslag onderscheidt offline regressie, live brononderzoek en handmatige browserinteractie. De repository bevat de reproduceerbare checks; er is geen verbinding met ChatGPT geclaimd.

## Offline

`dotnet restore --locked-mode`, `dotnet build --no-restore`, `dotnet test --no-build --no-restore`: build zonder waarschuwingen of fouten; **49 tests geslaagd**.

De tests gebruiken de geschoonde publieke Harvey Nash-fixture en gecontroleerde HTTP-/bronfakes. Ze dekken volledige HTML-tekst met lijsten/links, opdrachtgever, identiteit/deduplicatie, ontbrekende informatie, onafhankelijke ZZP/payroll, negaties, algemene voorwaarden, conflicten, nulbedragen, de tariefvoorbeelden en salarisranges. Ook: malformed HTTP-200-responses, bron-HTTP-fouten, lege resultaten, onbekende filters, onbevestigde locatie, timeouts vóór en na headers, cancellation, responsegrootte, details zonder zoekcache, afwezigheid versus onvolledige scan en een lege lokale pagina met latere match.

De sessietests controleren de classificatie van 401/403, redirects en login-HTML, publieke JSON versus beschermde JSON, en herkenbare vacaturecollecties versus foutobjecten. Deze gecontroleerde tests bewijzen geen werkende externe login; daarvoor is onderstaande praktijkproef uitgevoerd.

## Echte MCP-client en Harvey Nash live

```powershell
dotnet run --project tools/ZzpMcp.Smoke --no-build -- --config examples/stdio-client.json --live
```

Geslaagd:

- Client initialiseert een apart stdio-serverproces en ontdekt zes tools.
- Bronnenresponse toont één geïmplementeerde adapter; ongeldige paginagrootte en een verzonnen top-level filter geven expliciete `invalid_argument`, zonder bronrequest.
- Twee bronpagina's van drie vacatures leveren verschillende eerste ID's; bron gaf bij deze proef 42 treffers.
- Details bevatten de volledige genormaliseerde tekst, bron-ID en oorspronkelijke link. Eerste gecontroleerde ID: `3c0a4662-fe24-4bb4-b139-3dc4d1730dbe`, tekstlengte 5750 tekens.
- Een nieuw serverproces haalt dezelfde volledige tekst op zonder voorgaande zoekactie of cache.
- Unieke niet-bestaande query geeft een succesvolle lege lijst, onderscheiden van fouten.
- Een opdrachtgeverfilter met ontdekte facet-ID's geeft alleen de gevraagde opdrachtgever.
- De lokale contractfilter `zzp=not_allowed` is ook via het echte MCP-schema aangeroepen: uitsluitend negatieve contractstatus, met onbekend lokaal eindtotaal.
- Utrecht met 8,04672 km wordt naar 5 miles vertaald; bron bevestigt locatie-metadata en gaf zes treffers.

Losse reproduceerbare API-proeven bevestigden daarnaast lege query, `C#`, `C# SQL`, UUID-query, meerdere categoriewaarden, gecombineerde categorieën, page size 100 en beëindiging zonder vervolgtoken. Een oorspronkelijke vacaturelink gaf HTTP 200 met overeenkomende titel. De implementatie gebruikt offset, geen onbewezen tokenroute. Aantallen en vacature-inhoud zijn veranderlijk; dit is geen garantie van actuele beschikbaarheid of volledige operatorsemantiek.

De laatste code is tevens in Release gebouwd, met dezelfde 49 geslaagde tests en de volledige live smokecheck tegen de Release-DLL.

## Vertage/Striive-browserproef

Via de echte MCP-client gestart met `--browser`. Eigen zichtbaar Edge-profiel buiten de repository. De gebruiker logde zelf in en opende de opdrachtenpagina; geen wachtwoord via chat.

1. `browser_connect` keerde terug vóór menselijke login.
2. `browser_status` herhaalde een door de browser waargenomen GET op `freelancer.vertage.com`, route `/api/v2/job-requests`, met lokaal bewaarde headers/cookies. De leesactie gaf JSON. De identieke GET zonder sessie vereiste login. Status: `connected`, `protectedReadVerified=true`, resource `jobs`.
3. De gebruiker logde expliciet uit. De volgende controle rapporteerde `expired` en login vereist; geen lege vacaturelijst.
4. De gebruiker logde opnieuw in en opende opdrachten. De beschermde leesactie slaagde weer en de anonieme controle bleef login vereisen.

Dit toont intrekking door uitloggen en herstel via handmatige herlogin. Natuurlijke tijdsafhankelijke expiratie is niet apart afgewacht; MFA wordt door de gebruiker afgehandeld en is niet geautomatiseerd. De eerste technische proef faalde doordat HTTP/2-pseudoheaders werden herhaald; die worden nu uitgesloten. URLs, headerwaarden, tokens en JSON-inhoud van de ingelogde bron zijn niet in de repository of modeluitvoer opgeslagen.

De eindcontrole is hervat met een **eigen Chrome-profiel** (`ZZPMCP_BROWSER_CHANNEL=chrome`). De bestaande gewone Chrome-tab van de gebruiker stond los van de server; de gebruiker logde vervolgens in het aparte testvenster in. Het dashboard toont aanbevolen opdrachten via andere requests. Daarom opent de probe nu zelf de bekende opdrachtenroute `/inbox`, ook als login terugleidt naar `/dashboard`.

De uiteindelijke probe bevestigde live een herkenbare collectie van **118 vacatureobjecten** uit de door de gebruiker aangeleverde route `/api/v2/job-requests`. De identieke request zonder sessie vereiste login. Ook na sluiten en opnieuw starten van dit Chrome-profiel werkte de beschermde leesactie zonder nieuwe menselijke login. Dit is een geobserveerd sessiehergebruik, geen login zonder browser.

Ook de volledige herstelcontrole met de eindversie is geslaagd: expliciet uitloggen gaf `expired`; na menselijke herlogin herkende de probe het dashboard, opende zelf de opdrachtenlijst en herhaalde de oorspronkelijke bewaarde request-URL met opnieuw waargenomen headers. Resultaat: `connected`, `protectedReadVerified=true`, `recordCount=118`. De anonieme controle vereiste nog steeds login. Daarmee zijn collectievalidatie en herhalen van dezelfde zoekactie na herlogin live aangetoond. De browser-smokeclient is daarna normaal afgesloten met exitcode 0; het eigen lokale Chrome-profiel is behouden. De gewone Chrome-tab van de gebruiker is niet gewijzigd.

## Grenzen en vervolg

- Harvey Nash is de enige zoek-/detailadapter. Vertage/Striive is een losse lees-/sessieproef, geen genormaliseerde tweede bron. Circle8, Flextender en Freelance.nl zijn niet gebouwd.
- Geen login zonder browser, automatische MFA, cloudhosting of ChatGPT-koppeling aangetoond. Een bewaard browserprofiel is geen garantie dat iedere sessie na herstart bruikbaar blijft.
- Geen echte cv-matching uitgevoerd: de repo bevat een workflow voor de AI-host en een duidelijk gelabeld tabelsjabloon. Er is geen fictieve shortlist opgevoerd als live resultaat.
- Zoekmachine-operatoren, volledigheid van facetten, bronmutaties tijdens paginering en beschikbaarheid na publicatie blijven bronbeperkingen. Onvolledigheid/fouten worden zichtbaar gemaakt.
- Extractie blijft conservatief; vaardigheden worden niet los geïnterpreteerd en sommige feiten blijven onbekend. De volledige vacaturetekst en optionele oorspronkelijke velden blijven raadpleegbaar.
