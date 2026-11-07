# Openbare bronnen voor interim, freelance opdrachten en aanvullende vacatures

Peildatum: 6 november 2026. **Freep, MiPublic en Magnit zijn de beste nieuwe kandidaten naast de al geïmplementeerde Harvey Nash- en Flextender-koppelingen.** Ook Werken in Gelderland en LibLab zijn technisch eenvoudig toegankelijk, met waarschijnlijke inhoudelijke overlap met Flextender. Een account om te solliciteren is iets anders dan een account om de opdrachttekst te lezen.

Deze inventarisatie vergelijkt achttien bronnen, inclusief StarApple als aanvullende vacaturebron (toegevoegd op 7 november 2026). De overige waarnemingen behouden hun oorspronkelijke peildatum. Het oordeel betreft technische haalbaarheid op basis van kleine leesproeven, geen garantie dat alle opdrachten of alle velden toegankelijk zijn. Aantallen zijn momentopnamen en verschillen soms tussen live HTTP, browser en zoekmachinecache.

## Werkwijze

Eerst gewone anonieme HTTP-verzoeken. Alleen waar de opdrachtenlijst na JavaScript verschijnt, een aparte headless Chrome via Playwright. Geen computerbesturing, geen toegang tot open gebruikersbrowsers of profielen, geen accountregistraties en geen sollicitaties. Geen nieuwe productieadapters, wijzigingen aan de vacaturedatabase of volledige bronimports.

Bij een blokkade of expliciete accountgrens is gestopt. De eerder herhaaldelijk geweigerde Circle8-achtergrondbrowser is niet opnieuw geprobeerd. Computer Futures is na de eerste duidelijke Cloudflare-blokkade terzijde gelegd. Er zijn geen omzeilingspogingen, proxywissels of CAPTCHA-oplossingen gebruikt.

## Overzicht

| Bron | Zonder account volledige opdrachtinhoud? | Bewezen leesroute en paginering | Beoordeling |
| --- | --- | --- | --- |
| [Harvey Nash](https://www.harveynash.nl/vacatures) | Ja, bestaande adapter | Anonieme JSON-zoekroute; volledige tekst in resultaten; offsetpaginering reeds getest | **Al beschikbaar**. Niet opnieuw live onderzocht tijdens deze inventarisatie. |
| [Flextender](https://www.flextender.nl/opdrachten/) | Ja | Publiek formulier/Ajax levert alle client-side lijstpagina's; afzonderlijke detail-HTML; actuele implementatie en MCP-proef geslaagd | **Al geïmplementeerd**. Volledige tekst en provincie, geen geverifieerde kilometerfiltering. |
| [Freep](https://www.freep.nl/) | Ja, geteste detailpagina | Gewone HTML bevat Nuxt-payload met metadata van **470 opdrachten**; detailpagina heeft tekst en JobPosting JSON-LD. Headless klikken naar volgende pagina gaf andere slugs | **Hoogste prioriteit**. Browser is voor de uiteindelijke leesroute waarschijnlijk niet nodig. |
| [MiPublic ICT/Data](https://mipublic.nl/vakgebied/ict-data) | Ja, geteste detailpagina | Gewone HTML bevat ItemList met **154 van 154 detail-URL's**, ook buiten de zichtbare kaarten; detail-HTML en JobPosting JSON-LD | **Hoogste prioriteit**. Geen browser nodig voor de bewezen route. |
| [Magnit Source](https://magnit-source.magnitglobal.com/browse) | Ja, Nederlandse detailopdracht getest | Na headless inspectie zowel anonieme JSON-lijst als detail rechtstreeks met HTTP 200 gelezen. Twee cursorpagina's van tien opdrachten zonder overlap; totaal **115 wereldwijd** | **Hoge prioriteit**. JSON-route bruikbaar; Nederlandse selectie en klantportalen nog afzonderlijk onderzoeken. |
| [Werken in Gelderland inhuur](https://www.werkeningelderland.nl/inhuur/) | Ja | Normale HTML, tien detail-URL's per pagina; `/inhuur/page/2/` gaf tien andere opdrachten. Voorbeeldtekst volledig leesbaar; overzicht **78 opdrachten** | **Eenvoudig**, maar het officiële portaal verwijst voor reageren naar Flextender. Eerst aanvullend bereik bepalen. |
| [LibLab](https://liblab.nl/onze-opdrachten/) | Ja, geteste detailpagina | Normale HTML met twaalf kaarten; detailtekst en JobPosting JSON-LD. FacetWP-pagina 2 headless getest, daarna dezelfde `?_paged=2` met gewone HTTP 200 gelezen | **Eenvoudig**. Overlap met overheid-/Flextender-aanbod waarschijnlijk; geen technische blokkade. |
| [Freelancer.nl](https://freelancer.nl/opdrachten) | Ja, geteste omschrijving | Normale HTML; pagina 1 had 24 opdrachtlinks, `?page=2` bevatte 48, dus cumulatieve paginering | **Bruikbaar met beperkingen**. Opdrachtgevergegevens lijken deels gemaskeerd; reageren vraagt login. Niet verwarren met Freelance.nl. |
| [Freelapp IT](https://freelapp.nl/sectoren/it) | Uitgebreide beschrijving openbaar; gelijkheid met oorspronkelijke tekst niet vastgesteld | Gewone HTML, numerieke ID's, JobPosting JSON-LD en openbare verwijzing naar oorspronkelijke aanbieder. Pagina 2 gaf andere ID's | **Aanvullende aggregator**. Controleer de oorspronkelijke tekst voordat deze als volledig bronbewijs geldt. |
| [xFreelance](https://www.xfreelance.nl/opdrachten) | Lange omschrijving openbaar, maar bewerkt | Normale HTML; twintig detaillinks per pagina; `?page=2` gaf andere ID's. JSON-LD bevat uitgebreide tekst | **Aanvullend**. Contactgegevens verwijderd en oorspronkelijke bronlink vraagt account. Metadata kan botsen met contractvoorwaarden in de tekst. |
| [Opdracht Overheid](https://www.opdrachtoverheid.nl/) | Ja, geteste detailpagina | Lijst na JavaScript; headless knop `Meer Weergeven` leverde vijftig verschillende detail-URL's. Detail-HTML rechtstreeks HTTP 200; ongeveer **481 opdrachten** in overzicht | **Middelmatige inspanning**. Publieke JSON-zoekrequests waargenomen, zelfstandig HTTP-paginacontract nog niet vastgesteld. |
| [Freelance.nl](https://www.freelance.nl/opdrachten) | **Nee** voor geteste opdracht | Lijst headless leesbaar, publieke `/public-api`-requests waargenomen. Detail toont verkorte tekst en expliciete login-/registratievraag voor volledige opdracht | **Niet kiezen binnen accountvrije scope**. Gestopt bij de inhoudelijke toeganggrens. |
| [ItaQ leveranciersportaal](https://leveranciersportaal.itaq.nl/opdrachten) | **Nee volgens de eigen pagina** | Publieke pagina noemt het overzicht beperkt en vraagt login voor opdrachtgever en volledige omschrijving | **Niet kiezen binnen accountvrije scope**. Geen loginpogingen gedaan. |
| [Computer Futures](https://www.computerfutures.com/en-nl/find-a-job/) | Niet vastgesteld | Landingspagina en zoekscript HTTP 200; eigenlijke lijst headless HTTP **403**: `Attention Required! \| Cloudflare` en `Sorry, you have been blocked` | **Gestopt na blokkade**. Geen verdere pogingen. |
| [Circle8](https://www.circle8.nl/opdrachten) | Handmatig eerder wel; zelfstandig op de achtergrond niet betrouwbaar | Eerdere aparte achtergrondbrowsers herhaaldelijk HTTP **403**: `403: Forbidden` / `This request was blocked` | **Niet opnieuw geprobeerd**. Succes via het gewone gebruikersprofiel telt niet als accountvrije serverroute. Oorzaak van weigering niet vastgesteld. |
| [Vertage/Striive](https://freelancer.vertage.com/inbox) | **Nee voor de bestaande beschermde route** | Bestaande adapter gebruikt handmatige browserlogin; publieke vervanging niet onderzocht | **Buiten deze accountvrije uitbreiding**. Bestaande adapter blijft beschikbaar met login. |
| [Jellow](https://www.jellow.nl/faq) | Geen algemene openbare opdrachtenroute vastgesteld | Eigen FAQ beschrijft een profiel en selectie/uitnodiging door opdrachtgevers | **Geen kandidaat voor algemene accountvrije verzameling**. Alleen FAQ gelezen; geen profiel gemaakt. |
| [StarApple](https://www.starapple.nl/vacatures/) | Ja, geteste vacaturetekst | Anonieme HTML, `/vacatures/page/2/` en `/vacatures/page/18/` HTTP 200; respectievelijk 20, 20 en 4 verschillende links op de drie proefpagina's | **Technisch eenvoudig, aanvullende vacaturebron**. Geteste .NET-rol heeft arbeidsvoorwaarden en bruto maandsalaris; geen bewijs voor ZZP-toestemming. Sinds 7 november geïmplementeerd als `starapple`; zie [adapterdocumentatie](public-source-adapters.md). |

## Details van de beste kandidaten

### Freep

De HTML bevat `<script type="application/json" id="__NUXT_DATA__">`. Dit is een verwijzingsstructuur; indexnummers in de payload zijn geen stabiele veldnamen. Een array met 470 vacatures bevatte onder meer `id`, `slug`, `title`, `segment`, `location_province`, `company_name`, `hours` en `rate_max`. Dit maakte alle lijstidentiteiten toegankelijk zonder 47 zichtbare pagina's te doorlopen. De metadata bevat nog niet alle teksten.

De [detailproef Full stack ontwikkelaar](https://www.freep.nl/opdracht/full-stack-ontwikkelaar-13) leverde via gewone HTTP de opdrachtomschrijving, werkzaamheden en eisen. `.NET` en SharePoint komen in de beschrijving voor. De pagina bevat JobPosting JSON-LD. Locatiegegevens moeten worden gevalideerd: de proef bevatte Utrecht als plaats/regio, maar een postcodeveld dat daarmee niet zonder meer overeenkomt. Niet blind geocoderen of velden repareren zonder bronbewijs.

Voorstel: één overzichtsrequest, identiteiten uit de actuele payload, vervolgens alleen ontbrekende detailteksten. Geen hardgecodeerde payloadindexen en geen browser nodig zolang deze structuur blijft werken.

### MiPublic

De [ICT/Data-lijst](https://mipublic.nl/vakgebied/ict-data) bevat een CollectionPage/ItemList met 154 detail-URL's en `numberOfItems=154`. De zichtbare kaarten en knop `Laad meer opdrachten` tonen slechts een deel, maar de gevonden gestructureerde URL-lijst is volledig ten opzichte van dit opgegeven subtotaal. Dat bewijst nog geen volledige inventaris van alle vakgebieden.

De [detailproef Test Automation Engineer](https://mipublic.nl/vacature/test-automation-engineer-java-developer-met-testfocus) bevat opdrachtomschrijving, achtergrond en eisen, opdrachtgever DUO, uren en tarief. JobPosting heeft een identifier, plaats/regio en 6.875 tekens beschrijving. Bij implementatie de zichtbare tekst en JSON-LD vergelijken om eventuele afwijkingen of inkorting te herkennen.

Voorstel: begin met alle vakgebieden of één expliciet afgebakend vakgebied; bewaar vakgebieddekking en voorkom dat alleen ICT als hele bron wordt gepresenteerd.

### Magnit Source

De startpagina is een JavaScript-app. Eén headless inspectie maakte deze door de site gebruikte routes zichtbaar:

```text
POST https://app-openmarketgateway-prod.azurewebsites.net/api/jobsearch
GET  https://app-openmarketgateway-prod.azurewebsites.net/api/jobsearch/{uuid}/details
```

Beide routes zijn vervolgens met gewone HTTP, zonder accounts, cookies of gekopieerde authheaders, succesvol gelezen. De waargenomen eerste zoekbody:

```json
{"searchTerm":"","selectedStatuses":["4"],"pageSize":10,"sortOption":{"orderBy":"PublishedDate","direction":"Desc"}}
```

De response bevat `jobs`, `totalCount` en `continuationToken`. Door het ontvangen token als `continuationToken` in de volgende body mee te sturen kwamen tien andere ID's terug, zonder overlap met de eerste tien. De statuscode `4` is een waargenomen broncode, geen universele betekenis die andere portalen automatisch delen. De browser toonde hierbij `Status: Open`.

Een [Nederlandse detailopdracht](https://magnit-source.magnitglobal.com/browse/job/7c249ea8-12d6-415c-8b30-32becfb36415) was zonder account leesbaar. De JSON had `jobDescription`, `jobSkills`, `jobEducation`, `requiredSkills`, `company`, `clientInfo`, `requestDetails`, `candidateRequirements` en `technicalDetails`. De tekst bevatte werkzaamheden, voorwaarden en vereisten; de aanvraag had een afzonderlijk menselijk leesbaar Request ID naast de UUID. Het totaalaantal 115 omvatte ook Zweedse opdrachten en is geen Nederlands aantal.

Voorstel: publieke API-adapter, met aparte proef voor Nederlandfilter, contractvormen en eventuele klantportalen. Geen headless browser nodig voor de nu bevestigde lijst-/detailrequests.

## Aanvulling: StarApple (7 november 2026)

Aanvankelijk op verzoek uitsluitend geïnventariseerd als aanvullende vacaturebron, zonder database-import of adapter. Op later verzoek is de bron op 7 november 2026 geïmplementeerd: `starapple` ondersteunt zoeken, volledige details en hervatbaar verzamelen. Zie [broncontract en verificatie](public-source-adapters.md#starapple-tekst-contractvormen-en-verificatie); de onderstaande inventarisatie bewaart de oorspronkelijke kleine leesproef. De proef gebruikte vier anonieme HTTP-GET-verzoeken per meetronde (lijstpagina 1, 2 en 18 plus één detail), zonder browser, account of sollicitatie. Twee meetronden gaven dezelfde aantallen en dezelfde overlapuitkomst.

### Leesroute en omvang

- Overzicht: `GET https://www.starapple.nl/vacatures/`.
- Vervolgpagina's: `GET https://www.starapple.nl/vacatures/page/{nummer}/`.
- Detail: `GET https://www.starapple.nl/vacatures/{slug}/`.
- Pagina 1 en 2 leverden ieder twintig unieke detaillinks, zonder onderlinge overlap. De gelinkte laatste pagina 18 leverde vier. Alle drie pagina's samen bevatten 44 verschillende links. Alle geteste routes en de detailproef gaven HTTP 200 zonder login of JavaScript-uitvoering.
- Achttien pagina's met twintig plaatsen en vier links op de laatste pagina wijzen op **circa 344 vermeldingen**. Dit is een afleiding, geen volledig getelde broninventaris: pagina's 3–17 zijn niet gelezen en het aanbod kan tijdens paginering veranderen. De lijst bevat bovendien een open sollicitatie, zodat een vermeldingtotaal geen aantal concrete functies is.
- De zoekmachinecache toonde afwijkende inhoud en laatste-paginatotalen. Voor bovenstaande meetwaarden zijn de rechtstreekse HTTP-responses gebruikt; gecachete aantallen zijn niet gecombineerd met live pagina's.

### Volledige tekst en gegevens

De [detailproef Senior .NET Developer in Dordrecht](https://www.starapple.nl/vacatures/senior-net-developer-18/) bevat organisatiebeschrijving, functie, werkzaamheden, eisen en aanbod volledig in de HTML, vóór het sollicitatieformulier. C#, .NET, Azure en SQL Server worden genoemd. De vacature vermeldt 40 uur, €4.000–7.000 **bruto per maand**, vakantiegeld, vakantiedagen, pensioen en hybride werken met eenmaal per maand kantoor. De uiteindelijke werkgever wordt omschreven maar niet bij naam genoemd; StarApple is de bemiddelaar en mag niet automatisch als werkgever worden ingevuld.

De gevonden JSON-LD bevat een WordPress/Yoast-graaf met `WebPage`, `BreadcrumbList`, `WebSite` en `Organization`, maar **geen JobPosting**. Een toekomstige adapter moet dus de zichtbare vacaturesecties uitlezen. WebPage-publicatie-/wijzigingsdatums zijn niet zonder extra bewijs de publicatie- of sluitingsdatum van de vacature. Scripts, styles, recruiterkaart en sollicitatieformulier horen niet bij de functieomschrijving.

Het publieke GET-filterformulier bevat `s`, `levels[]`, `hours[]`, `function_groups[]`, `min_salary` en `max_salary`; de pagina toont ook locaties. Het filtercontract en de werking van afzonderlijke filters zijn niet getest. Het zichtbare kopje **Dienstverband** groepeert Management/Starter/Junior/Medior/Senior: dit zijn niveaus, geen bewijs voor loondienst, detachering of ZZP. Kilometerfiltering en salarisperioden op andere vacatures zijn niet vastgesteld.

### Beoordeling en afbakening

**Geschikt als afzonderlijk herkenbare vacaturebron; StarApple als dienstverlener bemiddelt breder dan reguliere banen.** De geteste arbeidsvoorwaarden wijzen op loondienst; die ene detailproef stelt niet vast welke verdeling van loondienst, ZZP, detachering en payroll op `/vacatures/` voorkomt. Een maandsalaris mag niet als uurtarief worden opgeslagen. Bepaal contractvormen per volledige tekst en behoud onzekerheid waar expliciete voorwaarden ontbreken.

Aanvullend contractonderzoek op 7 november 2026, naar aanleiding van de gebruikersinformatie dat StarApple payroll kan regelen voor niet-ZZP-opdrachten via Striive/Vertage:

- De [eigen dienstenpagina](https://www.starapple.nl/voor-opdrachtgevers/) noemt Interim, Detachering en Perm en beschrijft ook ondersteuning van professionals die willen freelancen. StarApple is dus geen uitsluitend op vaste banen gerichte partij.
- De openbare StarApple-publicatie [Payroll Manager, opdrachtnummer 316335](https://select.hr/opdrachten/starapple-intern/payroll-manager/1d8eca8c-9fe7-43c9-bff0-48e3e91fbe9c) vermeldt expliciet dat ZZP en detachering beide mogelijk zijn, met een verbod op doorleen. De pagina verwijst voor reageren naar Striive. De sluitingsdatum is 6 mei 2026; dit is contractbewijs uit een gepubliceerde opdracht, geen actuele sollicitatiemogelijkheid. Het woord Payroll in de functietitel beschrijft hier het vakgebied, niet de inzetconstructie.
- De openbare [Executive assistant-opdracht van Port of Rotterdam op Freep](https://www.freep.nl/opdracht/executive-assistant-2) vermeldt expliciet dat de rol beschikbaar is voor leveranciers/detacheerders of voor zelfstandigen die op de payroll bij StarApple Interim komen. Dit ondersteunt de door de gebruiker beschreven route; de concrete overeenkomst en acceptatie blijven per opdracht te bevestigen. Dit is bewijs uit een andere publicatieroute, niet uit de onderzochte StarApple-vacaturepagina's.

Voor de inventarisatie moeten **bron van de opdracht** en **mogelijke inzetconstructie** apart blijven. Een Vertage-opdracht met ZZP niet toegestaan kan voor de gebruiker nog relevant zijn wanneer payroll expliciet is toegestaan of afzonderlijk bevestigd wordt. StarApple als mogelijke payrollpartij bewijst op zichzelf niet dat iedere opdrachtgever die constructie accepteert. Noteer daarom afzonderlijk: rechtstreekse ZZP-inzet toegestaan/uitgesloten/onbekend; payroll toegestaan/uitgesloten/onbekend; leverancier/werkgever en bronbewijs. Sluit een opdracht niet uitsluitend op een negatief ZZP-label uit wanneer de gebruiker ook payroll wil onderzoeken.

De site vermeldt dat StarApple powered by Vertage is. Dit bewijst geen inhoudelijke gelijkheid met de beschermde freelancerlijst van Vertage; die bestaande adapter en deze publieke vacaturebron hebben verschillende leesroutes. Eventuele overlap moet later op bronverwijzingen en inhoud worden vastgesteld.

Bij eventuele bouw: een eigen bron-ID `starapple`, native HTML-paginering met controle op herhaalde pagina's, volledige sectieteksten, salaris met periode, en expliciete scheiding tussen open sollicitatie en concrete vacature. Eerst de tussenpagina's en meerdere detailvarianten testen. Het aanvullende bewijs over ZZP/payroll komt van andere publicatieroutes; de dekking daarvan door `/vacatures/` is niet vastgesteld. De bouwvolgorde blijft vooralsnog ongewijzigd.

Lokale bewijsbestanden: `.artifacts/source-inventory/starapple/` (genegeerd door Git); proefscript `.artifacts/starapple_probe.py`. Geen accountgegevens gebruikt of opgeslagen.

## Herkomst en overlap

[Werken in Gelderland bevestigt](https://www.werkeningelderland.nl/uitleg-inhuur/) de overstap naar het DAS van Flextender op 3 april 2025. De gecontroleerde WMO/Jeugd-opdracht verwijst voor reageren naar Flextender. Dat is direct herkomstbewijs voor deze route, maar niet automatisch bewijs dat iedere tekst of deadline gelijk is.

De geteste xFreelance-opdracht noemt Flextender als bron. De kaart noemt `Freelance`, terwijl de omschrijving begint met een detacheringsopdracht en een verplichte arbeidsovereenkomst. Dat is een concreet voorbeeld waarom de agent volledige contractvoorwaarden moet lezen en niet alleen lijstlabels mag volgen. Ook contactinformatie is in de overgenomen tekst geredigeerd.

Freelapp noemt bij de geteste NIS2-opdracht Consultant.nl en geeft een openbare oorspronkelijke link. Bewaar bij een toekomstige koppeling zowel aggregator-ID als oorspronkelijke URL. Eén opdracht kan zo op verschillende sites verschijnen; titelovereenkomst alleen is onvoldoende voor samenvoeging. Deze inventarisatie heeft geen vacatures samengevoegd of verwijderd.

## Fouten en toeganggrenzen

| Bron of kanaal | Waarneming | Vervolg |
| --- | --- | --- |
| Computer Futures headless lijst | HTTP 403; `Attention Required! \| Cloudflare`; `Sorry, you have been blocked` | Meteen gestopt, niet geprobeerd te omzeilen. |
| Circle8 achtergrondbrowser | Eerder twee aparte proeven met HTTP 403; `This request was blocked` | In deze inventarisatie geen nieuw bezoek. |
| Freelance.nl detail | `Volledige opdracht bekijken?` gevolgd door login/registratie | Geen verdere detailrequests of accountaanmaak. |
| ItaQ | Eigen toelichting: login nodig voor alle details, inclusief opdrachtgever en volledige omschrijving | Geen verdere technische ontsluiting geprobeerd. |
| Jellow | Eigen FAQ beschrijft profiel en uitnodiging/selectie | Geen account aangemaakt en geen besloten opdrachten gezocht. |
| Webzoektool bij Opdracht Overheid, Freep en MiPublic | Enkele pagina's waren via die tool niet toegankelijk, terwijl directe HTTP-requests HTTP 200 en inhoud gaven | Niet als blokkade van de bron geclassificeerd; de werkende HTTP-route is het bewijs. Geen herhaalde webtoolpogingen. |

## Advies voor de bouwvolgorde

1. **Freep**: alle lijstidentiteiten in één publieke response en geteste volledige details; breed aanbod.
2. **MiPublic**: publieke URL-inventaris en rijke detailvelden; technisch weinig browserafhankelijkheid.
3. **Magnit**: anonieme JSON met bewezen cursorpaginering en Nederlandse detailtekst; eerst Nederlandse scope vaststellen.
4. **LibLab**: publieke HTML en geteste paginering; bruikbaar extra kanaal, overlap analyseren.
5. **Werken in Gelderland**: zeer eenvoudige HTML, maar aanvullende dekking naast Flextender moet het nut bepalen.

Freelancer.nl, Freelapp en xFreelance zijn extra kanalen met beperkingen rond opdrachtgever, oorspronkelijke tekst of herkomstlink. Opdracht Overheid is een bruikbare vervolgstap zodra het anonieme zoek-/paginacontract zelfstandig is vastgelegd. De andere bronnen blijven buiten deze accountvrije bouwronde.

De testproeven en technische waarnemingen staan lokaal in de genegeerde map `.artifacts/source-inventory/`. Er zijn geen cookies, authheaders of accountgegevens in dit verslag opgenomen. Vluchtige paginatokens en runtimebestanden zijn geen onderdeel van het adaptercontract.
