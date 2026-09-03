namespace StubHub.Fetch;

/// <summary>
/// The in-page JavaScript StubHubClient runs via
/// <see cref="DataDomeBrowser.EvaluateJsonAsync"/>. Each constant is ONE arrow
/// function expression <c>async (arg) =&gt; { ... }</c>, transcribed verbatim from
/// the same blobs in python/stubhub/client.py (<c>_JS_BOOTSTRAP</c> /
/// <c>_JS_GRID_POST</c> / <c>_JS_SECTION_BATCH</c>), with <c>COMMON_QS</c> already
/// spliced in and <c>_JS_HELPERS</c> concatenated where the Python side splices it.
///
/// Result flows back through <c>window.chrome.webview.postMessage</c>, not the
/// script return value - see the NOTE in
/// BroadwayDirect.Fetch/ProxyEnvironmentPool.cs for why ExecuteScriptAsync's
/// return value can't be trusted for Promise-returning scripts.
/// </summary>
internal static class StubHubScripts
{
    private const string CommonQs = "?estimatedFees=false&quantity=0&sortDirection=1&sortBy=PRICE";

    // Balanced [..]/{..} extractor, ignoring braces inside strings. Same logic
    // as JsonTokenExtractor.ExtractJsonToken, run in the page for speed.
    private const string Helpers = """"
      function __ex(text, marker){
        const m = text.indexOf(marker); if(m<0) return null;
        let i = m + marker.length;
        while(i<text.length && ' \t\r\n:'.indexOf(text[i])>=0) i++;
        if(i>=text.length || '[{'.indexOf(text[i])<0) return null;
        const start=i; let dS=0,dC=0,inStr=false,esc=false;
        for(; i<text.length; i++){ const c=text[i];
          if(esc){esc=false;continue;}
          if(c==='\\' && inStr){esc=true;continue;}
          if(c==='"'){inStr=!inStr;continue;}
          if(inStr) continue;
          if(c==='[')dS++; else if(c==='{')dC++; else if(c===']')dS--; else if(c==='}')dC--;
          if(dS===0 && dC===0){ i++; break; }
        }
        try { return JSON.parse(text.slice(start,i)); } catch(e){ return null; }
      }
      function __gridItems(html){
        let v = __ex(html, '"grid":{"items":');
        if(v===null){ const g=html.indexOf('"grid":'); if(g>=0) v=__ex(html.slice(g), '"items":'); }
        return v || [];
      }
    """";

    /// <summary>Bootstrap off the SSR HTML (fetched in-page). Returns
    /// <c>{ error: ... }</c> on a challenge / non-200.</summary>
    public static readonly string Bootstrap = "async (basePath) => {" + Helpers + """"
      const r = await fetch(basePath + '?estimatedFees=false&quantity=0&sortDirection=1&sortBy=PRICE', { credentials: 'include' });
      if(r.status !== 200) return { error: 'status ' + r.status };
      const h = await r.text();
      if(/Please enable JS|captcha-delivery/i.test(h)) return { error: 'datadome challenge' };

      let sportsEvent = null;
      const ldRe = /<script[^>]+application\/ld\+json[^>]*>([\s\S]*?)<\/script>/g;
      let mm;
      while((mm = ldRe.exec(h))){
        try { const d = JSON.parse(mm[1].trim());
          if(d && (d['@type']==='SportsEvent' || d['@type']==='Event')){ sportsEvent = d; break; }
        } catch(e){}
      }
      const tcM = h.match(/"totalCount":(\d+)/);
      const nameM = h.match(/"eventName":"((?:[^"\\]|\\.)*)"/);
      const venM = h.match(/"venueName":"((?:[^"\\]|\\.)*)"/);
      const vidM = h.match(/"venueId":(\d+)/);
      const vcfgM = h.match(/"venueConfigId":(\d+)/);
      const fdtM = h.match(/"formattedEventDateTime":"([^"]+)"/);
      const fssM = h.match(/"filterSortSessionId":"([^"]+)"/);
      const catM = h.match(/"categoryId":(\d+)/);
      const eidM = basePath.match(/\/event\/(\d+)/);
      return {
        eventId: eidM ? eidM[1] : ((sportsEvent && String(sportsEvent.url||'').match(/\/event\/(\d+)/)||[])[1] || ''),
        eventName: (sportsEvent && sportsEvent.name) || (nameM && JSON.parse('"'+nameM[1]+'"')) || '',
        venueName: (venM && JSON.parse('"'+venM[1]+'"')) ||
                   (sportsEvent && sportsEvent.location && sportsEvent.location.name) || '',
        venueId: vidM ? parseInt(vidM[1],10) : null,
        venueConfigId: vcfgM ? parseInt(vcfgM[1],10) : null,
        formattedEventDateTime: fdtM ? fdtM[1] : '',
        totalCount: tcM ? parseInt(tcM[1],10) : null,
        filterSortSessionId: fssM ? fssM[1] : null,
        categoryId: catM ? parseInt(catM[1],10) : null,
        ticketClasses: __ex(h, '"ticketClasses":') || [],
        ticketClassPopupData: __ex(h, '"ticketClassPopupData":') || {},
        sectionPopupKeys: Object.keys(__ex(h, '"sectionPopupData":') || {}),
        sportsEvent: sportsEvent,
      };
    }
    """";

    /// <summary>Primary: one POST to the grid endpoint with a large PageSize,
    /// using the page's own filterSortSessionId.</summary>
    public const string GridPost = """"
    async (arg) => {
      const body = JSON.stringify({
        ShowAllTickets: true, PageSize: arg.pageSize, CurrentPage: arg.currentPage,
        SortBy: "PRICE", SortDirection: 1, Sections: "", TicketClasses: "",
        PriceRange: "", FilterSortSessionId: arg.sessionId, Method: "IndexSh",
        CategoryId: arg.categoryId || 0
      });
      const paths = [arg.basePath + 'grid', '/event/' + arg.eid + '/grid'];
      for (const p of paths) {
        try {
          const r = await fetch(p, { method: 'POST', credentials: 'include',
            headers: { 'Content-Type': 'application/json' }, body });
          if (r.status !== 200) continue;
          let j;
          try { j = await r.json(); } catch (e) { continue; }
          const g = (j && j.grid) ? j.grid : (j || {});
          const items = g.items || j.items || [];
          const tc = (g.totalCount != null ? g.totalCount
                     : (j.totalCount != null ? j.totalCount : null));
          return { status: 200, path: p, items: items, totalCount: tc };
        } catch (e) {}
      }
      return { status: 0 };
    }
    """";

    /// <summary>Fallback / gap-fill: fetch a batch of section specs (<c>{sec, tc}</c>)
    /// in parallel; every section holds &lt;=10 listings so no pagination. A spec with
    /// a non-empty <c>tc</c> is a premium ticket class whose section returns empty
    /// under a bare <c>&amp;sections=</c> - it needs <c>&amp;ticketClasses=&lt;tc&gt;</c>
    /// too (extraction-report Step 6).</summary>
    public static readonly string SectionBatch = "async (arg) => {" + Helpers + """"
      const specs = arg.specs, basePath = arg.basePath;
      const failed = [];
      const results = await Promise.all(specs.map(sp => {
        const tcq = sp.tc ? ('&ticketClasses=' + sp.tc) : '';
        return fetch(basePath + '?estimatedFees=false&quantity=0&sortDirection=1&sortBy=PRICE' + tcq + '&sections=' + sp.sec, { credentials: 'include' })
          .then(async r => {
            if(r.status !== 200){ failed.push(sp); return []; }
            return __gridItems(await r.text());
          })
          .catch(() => { failed.push(sp); return []; });
      }));
      return { items: results.flat(), failed };
    }
    """";

    // Referenced only to keep the CommonQs literal meaningful in docs/diffs -
    // the query string is inlined verbatim in Bootstrap / SectionBatch above,
    // exactly as python/stubhub/client.py splices COMMON_QS with `% COMMON_QS`.
    public static string CommonQueryString => CommonQs;
}
