# Harvey Nash: onderzochte API en adapter

Waargenomen op 5 november 2026. Dit is een intern website-endpoint met live geverifieerd gedrag, geen garantie van een stabiel officieel API-contract.

```powershell
Invoke-RestMethod 'https://www.harveynash.nl/_sf/api/v1/jobs/search.json' `
  -Method Post -ContentType 'application/json' `
  -UserAgent 'ZZPMcp/0.1' `
  -Body (Get-Content examples/harvey-nash-request.json -Raw)
```

Geen authenticatie nodig in de uitgevoerde proeven. Een .NET-request zonder User-Agent gaf 403; dezelfde client met `ZZPMcp/0.1` werkte. Een 403 bewijst op zichzelf dus geen verlopen login: de foutmelding meldt geweigerde toegang.

| Onderdeel | Waarneming en implementatie |
| --- | --- |
| Lege query | 42 resultaten bij de proef. Dit aantal is geen fixtureverwachting voor toekomstige live tests. |
| Query | `C#` en `C# SQL` gaven één resultaat; een unieke niet-bestaande zoekterm nul. Tokenisatie, synoniemen en logische operators zijn niet volledig vastgesteld. De AI-host voert zo nodig meerdere rondes uit. |
| ID zoeken | De exacte bron-UUID als `query` gaf die vacature terug. `get_job` eist een exacte ID-match en gebruikt deze route eerst; er is geen verzonnen detailendpoint. |
| Terugzoeken | Als de ID-query niets exacts geeft: lege zoekquery in maximaal tien pagina's van 100, totale deadline 60 s. Volledig onderzocht zonder ID geeft `not_found`; grens, voortijdig lege of herhaalde pagina geeft `search_incomplete`. Geen cacheafhankelijkheid. |
| Locatie | Utrecht, radius 5 miles gaf zes resultaten en niet-lege `metadata.location_filters`. MCP accepteert km, conversie `km / 1.609344`. Zonder bevestigde locatie-metadata is een gevraagde locatie een expliciete fout. Het betreft brongeocodering, geen reistijd- of afstandsgarantie. |
| Categorieën | `filters: { categoryId: [valueId] }`. Clients/Gasunie gaf zeven. Enexis of Gasunie in één categorie gaf 33 en alleen die opdrachtgevers. Gasunie plus Employment type/Interim gaf zeven. Overige logische nuances/hierarchieën zijn niet volledig onderzocht. |
| Facetten | `histogram_results` levert categorie-ID/naam en `counts` met waarde-ID/naam/aantal. Keuzes worden aan de host getoond; validatie gebeurt tegen een actuele bronbrede lege zoekactie. Aantallen zijn bronwaargenomen, geen volledige vaste taxonomie. |
| Paginering | `offset=0` en `offset=3`, beide page size 3, gaven verschillende bron-ID's. Page size 100 gaf alle 42 en geen token. Offset is de gebruikte route. `metadata.next_page_token` is waargenomen maar niet gebruikt: token-requestsemantiek is niet vastgesteld. |
| Volledige tekst | `results[].job.description` bevat HTML. Geen extra detailrequest nodig om die volledige broninhoud te krijgen. |
| Bronlink | `https://www.harveynash.nl/vacatures/{url_slug}` gaf live HTTP 200 met overeenkomende titel. |

De client verwerkt minimaal geldige `results` en `total_size` plus `id`, `title`, `description` en `url_slug` per job. Extra bronvelden blijven toegestaan; ontbrekende/ongeldige kernvelden geven `source_schema`. HTML, foutobjecten of malformed JSON met HTTP 200 zijn geen lege succesvolle lijst. HTTP-fouten worden zonder bronbody doorgegeven als bruikbare foutcode.

## Mapping

- `Clients` → opdrachtgever; locatie uit `addresses`, anders `original_location`.
- Bron-ID blijft identiteit; `external_reference`, oorspronkelijke velden en HTML zijn optioneel via `includeRaw` beschikbaar. Geen samenvoeging van gelijkende titels of verschillende bronnen.
- Unix-seconden → tijdstempels met offset; `fetchedAt` is de werkelijke response-ophaaltijd. Een toekomstige vervaldatum bewijst niet dat solliciteren nog kan.
- HTML → tekst met paragrafen, lijstpunten en linkdoelen; uitvoerbare HTML wordt verwijderd.
- Uren, hybride, start en duur alleen uit herkenbare gelabelde regels met bewijs. Eisen alleen uit herkenbare eisensecties. Vaardigheden blijven een lege niet-geëxtraheerde lijst; de volledige tekst is leidend.
- Contractuitspraken worden conservatief herkend; onder meer `ZZP mogelijk: Ja/Nee`, `geen ZZP`, `ZZP niet mogelijk`, `ZZP toegestaan`. Algemene voorwaarden, Interim en detachering zijn geen positieve contractuitspraken. ZZP en payroll blijven gescheiden.
- Geld houdt bronvermeldingen afzonderlijk. `€78,85` en `€78.85` worden herkend; `Tarief in overleg` blijft zonder bedrag; `Richttarief 115 EUR all-in ex BTW` bewaart bedrag/voorwaarden zonder eenheid te raden. Onduidelijke duizendtalnotatie blijft onbekend met de ruwe tekst. Meerdere vermeldingen worden niet stilzwijgend samengevoegd.

Fixture: `tests/ZzpMcp.Tests/Fixtures/harvey-nash.sanitized.json`, afgeleid van de eerste drie publieke live-resultaten. Consultantvelden/-categorie, request-ID en paginatoken zijn weggelaten; `total_size` is voor de zelfstandige fixture op drie gezet. Histogrammen behouden de bronwaargenomen waarden uit de oorspronkelijke zoekactie. Vacature-ID's, categorieën, volledige HTML en relevante nul-/tariefvelden zijn behouden. Dit is een ingekorte testfixture, geen ongewijzigde live response of actuele vacaturelijst.

Gebruikte officiële technische bronnen: [MCP C# SDK 2.3.1 getting started](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.1/docs/concepts/getting-started.md), [.NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview), [AngleSharp basics](https://github.com/AngleSharp/AngleSharp/blob/devel/docs/general/01-Basics.md), [Playwright persistent context](https://playwright.dev/dotnet/docs/api/class-browsertype#browser-type-launch-persistent-context). Pakketversies: MCP 2.3.1, Hosting 10.0.0, AngleSharp 1.9.1, Playwright 1.64.1. De build en tests controleren de gebruikte API's tegen die concrete packages.
