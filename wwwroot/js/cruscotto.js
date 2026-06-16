    const REFRESH_MS = 5000;
    document.getElementById('refreshSec').textContent = (REFRESH_MS/1000).toFixed(0);

    // Map of SB queue name -> SVG node id whose class we recolor + edge particle id we toggle.
    const QUEUE_TO_NODE = {
      'action-requests' : { node:'n-areq',      part:'part-areq-proc',      metricId:'m-areq'      },
      'action-dispatch' : { node:'n-disp',      part:'part-proc-disp',      metricId:'m-disp'      },
      'wipe-action'     : { node:'n-wipe',      part:'part-disp-wipe',      metricId:'m-wipe'      },
      'autopilot-action': { node:'n-autopilot', part:'part-disp-autopilot', metricId:'m-autopilot' },
      'bitlocker-action': { node:'n-bitlocker', part:'part-disp-bitlocker', metricId:'m-bitlocker' },
      'rename-action'   : { node:'n-rename',    part:'part-disp-rename',    metricId:'m-rename'    },
    };

    function setNodeClass(id, cls) {
      const el = document.getElementById(id);
      if (!el) return;
      el.classList.remove('green','yellow','red','gray');
      el.classList.add(cls || 'gray');
    }
    function setText(id, txt) {
      const el = document.getElementById(id);
      if (el) el.textContent = txt;
    }
    function pillClass(status) {
      switch ((status||'').toLowerCase()) {
        case 'green':  return 'green';
        case 'yellow': return 'yellow';
        case 'red':    return 'red';
        default:       return 'gray';
      }
    }

    function render(data) {
      setText('lastUpdate', 'aggiornato ' + new Date(data.generatedAt).toLocaleTimeString());

      // Queues + corresponding capability nodes
      const qBody = document.getElementById('queueTable');
      qBody.innerHTML = '<tr><th>Coda</th><th>Att.</th><th>DLQ</th></tr>';
      for (const q of (data.queues||[])) {
        const meta = QUEUE_TO_NODE[q.name];
        const cls  = pillClass(q.status);
        if (meta) {
          setNodeClass(meta.node, cls);
          setText(meta.metricId, `act=${q.active}  dlq=${q.deadLetter}`);
          const p = document.getElementById(meta.part);
          if (p) p.classList.toggle('hidden', q.active === 0 && q.scheduled === 0);
          // Color the inbound edge accordingly
          const edgeId = 'e-' + meta.part.replace(/^part-/, '');
          const edgeEl = document.getElementById(edgeId);
          if (edgeEl) edgeEl.classList.toggle('live', q.active > 0);
        }
        const tr = document.createElement('tr');
        tr.innerHTML = `<td>${q.name}</td><td>${q.active}</td><td>${q.deadLetter}</td>`;
        qBody.appendChild(tr);
      }

      // Web + Proc nodes: derive health from action-requests + action-dispatch queues
      const arStatus   = (data.queues||[]).find(q => q.name === 'action-requests')?.status   || 'gray';
      const dispStatus = (data.queues||[]).find(q => q.name === 'action-dispatch')?.status || 'gray';
      setNodeClass('n-web',  pillClass(arStatus));
      setNodeClass('n-proc', pillClass(dispStatus));
      setText('m-web',  '/api/actions');
      setText('m-proc', 'dispatcher');

      // Ledger panel
      const L = data.ledger || {};
      const pill = document.getElementById('ledgerPill');
      pill.className = 'pill ' + pillClass(L.status);
      pill.textContent = (L.status||'—').toUpperCase();
      setText('ledgerTotal', L.totalEntries ?? '—');
      setText('ledgerStuck', L.stuckEntries ?? '—');
      setText('ledgerOldest', L.oldestStuckIssuedAt
        ? `${new Date(L.oldestStuckIssuedAt).toLocaleString()}  (${L.oldestStuckIntuneDeviceId||''})`
        : '—');
      setText('ledgerGrace', (L.graceHours ?? '—') + ' h');

      // Warnings
      const wEl = document.getElementById('warnings');
      const ws = data.warnings || [];
      if (ws.length === 0) {
        wEl.innerHTML = '<div class="empty">nessun warning</div>';
      } else {
        wEl.innerHTML = '';
        for (const w of ws) {
          const d = document.createElement('div');
          d.className = 'warn' + (w.startsWith('Queue') && w.includes('backlog') ? ' warn-yellow' : '');
          d.textContent = w;
          wEl.appendChild(d);
        }
      }

      // Live dot
      document.getElementById('liveDot').style.background = ws.length ? 'var(--red)' : 'var(--green)';

      renderDiagnostics(data.diagnostics);
      renderStuckList(L.topStuck || []);
      renderFlowDiagram(data);
    }

    // ─── Flow diagram (Intune.Up-style boxes + arrows + edge labels) ────────
    // Layout:
    //   Top row    (intake → dispatch):  Web | action-requests | Proc | action-dispatch | Status Poller
    //   Bottom row (capability runners): Wipe | Autopilot | BitLocker | Rename
    //   Edges:
    //     - Top row: arrows between sequential boxes; label above shows act/dlq for the *next* queue
    //     - Vertical fan-out from action-dispatch down to each capability runner
    const FD_NS = 'http://www.w3.org/2000/svg';
    const FD_NODE_W = 200, FD_NODE_H = 78;
    const FD_TOP_Y = 20, FD_BOT_Y = 280;
    // Top row x positions (5 boxes, 240 spacing, gap 40 between them).
    const FD_TOP = [
      { id:'web',   x:20,   label:'Web (intake)',   sub:'HTTP /api/actions' },
      { id:'areq',  x:260,  label:'Queue',          sub:'action-requests',   queue:'action-requests' },
      { id:'proc',  x:500,  label:'Proc',           sub:'dispatcher' },
      { id:'adisp', x:740,  label:'Queue',          sub:'action-dispatch',   queue:'action-dispatch' },
      { id:'poll',  x:980,  label:'Status Poller',  sub:'observes terminal state' },
    ];
    // Bottom row: 4 capability runners aligned roughly under the dispatch fan-out.
    const FD_BOT = [
      { id:'wipe',  x:140,  label:'Wipe',      sub:'wipe-action',      queue:'wipe-action' },
      { id:'autop', x:420,  label:'Autopilot', sub:'autopilot-action', queue:'autopilot-action' },
      { id:'bitl',  x:700,  label:'BitLocker', sub:'bitlocker-action', queue:'bitlocker-action' },
      { id:'renm',  x:980,  label:'Rename',    sub:'rename-action',    queue:'rename-action' },
    ];

    function fdColors(status) {
      switch ((status||'').toLowerCase()) {
        case 'green':  return { fill:'#f0fdf4', stroke:'#16a34a', text:'#065f46', marker:'fd-arrow-ok',   line:'#16a34a' };
        case 'yellow': return { fill:'#fffbeb', stroke:'#d97706', text:'#92400e', marker:'fd-arrow-warn', line:'#d97706' };
        case 'red':    return { fill:'#fef2f2', stroke:'#dc2626', text:'#991b1b', marker:'fd-arrow-err',  line:'#dc2626' };
        default:       return { fill:'#f9fafb', stroke:'#9ca3af', text:'#374151', marker:'fd-arrow-unk',  line:'#9ca3af' };
      }
    }

    function fdEdgeWidth(active) {
      const v = Number(active) || 0;
      if (v <= 0)  return 2;
      if (v < 5)   return 3;
      if (v < 25)  return 4;
      if (v < 100) return 6;
      return 8;
    }

    function fdEl(tag, attrs, children) {
      const e = document.createElementNS(FD_NS, tag);
      if (attrs) for (const k of Object.keys(attrs)) e.setAttribute(k, attrs[k]);
      if (children) for (const c of children) e.appendChild(c);
      return e;
    }
    function fdText(x, y, txt, opts) {
      opts = opts || {};
      const t = fdEl('text', {
        x: x, y: y,
        'text-anchor': opts.anchor || 'middle',
        'font-size':   opts.size   || 12,
        'font-weight': opts.weight || 400,
        fill:          opts.fill   || '#111827'
      });
      t.textContent = txt == null ? '' : String(txt);
      return t;
    }

    function fdBox(spec, queue) {
      const status = queue ? queue.status : (spec.queue ? 'gray' : 'green');
      const c = fdColors(status);
      const g = fdEl('g');
      g.appendChild(fdEl('rect', {
        x: spec.x, y: spec.y, width: FD_NODE_W, height: FD_NODE_H,
        rx: 8, ry: 8, fill: c.fill, stroke: c.stroke, 'stroke-width': 2
      }));
      // Label (top)
      g.appendChild(fdText(spec.x + FD_NODE_W/2, spec.y + 20, spec.label, { weight:600, fill:'#111827' }));
      // Primary metric (middle)
      const primary = queue
        ? `act ${queue.active}  ·  dlq ${queue.deadLetter}`
        : (spec.primary || '—');
      g.appendChild(fdText(spec.x + FD_NODE_W/2, spec.y + 42, primary, { size:13, weight:700, fill:c.text }));
      // Sub (bottom)
      g.appendChild(fdText(spec.x + FD_NODE_W/2, spec.y + 62, spec.sub || '', { size:10, fill:'#6b7280' }));
      // Tooltip
      const title = fdEl('title');
      title.textContent = queue
        ? `${spec.sub || spec.label}\nactive=${queue.active}, dlq=${queue.deadLetter}, scheduled=${queue.scheduled}\nstatus=${queue.status}${queue.error ? '  (' + queue.error + ')' : ''}`
        : (spec.label + (spec.sub ? ' — ' + spec.sub : ''));
      g.appendChild(title);
      return g;
    }

    function fdArrowH(fromSpec, toSpec, queue) {
      const c = fdColors(queue ? queue.status : 'gray');
      const w = fdEdgeWidth(queue ? queue.active : 0);
      const x1 = fromSpec.x + FD_NODE_W;
      const x2 = toSpec.x;
      const y  = FD_TOP_Y + FD_NODE_H/2;
      const g  = fdEl('g');
      g.appendChild(fdEl('line', {
        x1: x1, y1: y, x2: x2, y2: y,
        stroke: c.line, 'stroke-width': w, 'marker-end': `url(#${c.marker})`, 'stroke-linecap':'round'
      }));
      // Edge label above midpoint
      if (queue) {
        const midX = (x1 + x2) / 2;
        g.appendChild(fdText(midX, y - 8, `act ${queue.active} · dlq ${queue.deadLetter}`, { size:11, weight:500, fill:'#1f2937' }));
      }
      return g;
    }

    function fdArrowV(fromSpec, toSpec, queue) {
      // Verticale dal centro bottom del top box al centro top del bottom box.
      const c = fdColors(queue ? queue.status : 'gray');
      const w = fdEdgeWidth(queue ? queue.active : 0);
      const x1 = fromSpec.x + FD_NODE_W/2;
      const y1 = FD_TOP_Y + FD_NODE_H;
      const x2 = toSpec.x + FD_NODE_W/2;
      const y2 = FD_BOT_Y;
      const g  = fdEl('g');
      // Z-shaped polyline so non-aligned columns join cleanly:
      //   (x1,y1) → (x1, midY) → (x2, midY) → (x2, y2)
      const midY = (y1 + y2) / 2;
      const pts = `${x1},${y1} ${x1},${midY} ${x2},${midY} ${x2},${y2}`;
      g.appendChild(fdEl('polyline', {
        points: pts, fill:'none', stroke: c.line, 'stroke-width': w,
        'marker-end': `url(#${c.marker})`, 'stroke-linecap':'round', 'stroke-linejoin':'round'
      }));
      if (queue) {
        // Etichetta accanto al primo segmento orizzontale
        g.appendChild(fdText(x2, midY - 6, `act ${queue.active} · dlq ${queue.deadLetter}`, { size:11, weight:500, fill:'#1f2937' }));
      }
      return g;
    }

    function renderFlowDiagram(data) {
      const svg = document.getElementById('flowDiag');
      if (!svg) return;
      // Cancella tutto eccetto <defs>
      for (let i = svg.childNodes.length - 1; i >= 0; i--) {
        const ch = svg.childNodes[i];
        if (ch.nodeType !== 1 || ch.nodeName.toLowerCase() !== 'defs') {
          svg.removeChild(ch);
        }
      }
      const queues = (data.queues || []);
      const byName = {};
      for (const q of queues) byName[q.name] = q;

      // Top: positioning Y
      const top = FD_TOP.map(s => Object.assign({ y: FD_TOP_Y }, s));
      const bot = FD_BOT.map(s => Object.assign({ y: FD_BOT_Y }, s));

      // Edges top: web→areq→proc→adisp→poll (label = next queue if any)
      for (let i = 0; i < top.length - 1; i++) {
        const q = top[i+1].queue ? byName[top[i+1].queue] : null;
        svg.appendChild(fdArrowH(top[i], top[i+1], q));
      }
      // Edges vertical: action-dispatch (top[3]) fan-out to each bottom box
      const dispatch = top[3];
      for (const b of bot) {
        const q = b.queue ? byName[b.queue] : null;
        svg.appendChild(fdArrowV(dispatch, b, q));
      }
      // Nodes — disegnati DOPO le frecce così sono sopra
      for (const s of top) svg.appendChild(fdBox(s, s.queue ? byName[s.queue] : null));
      for (const s of bot) svg.appendChild(fdBox(s, s.queue ? byName[s.queue] : null));
    }

    function renderDiagnostics(diag) {
      const el = document.getElementById('diagBody');
      if (!diag) { el.innerHTML = '<div class="empty">non disponibile</div>'; return; }
      const lines = [];
      const lastTick = diag.pollerLastTick ? new Date(diag.pollerLastTick).toLocaleString() : '—';
      const hCls = (diag.pollerHealth||'').toLowerCase();
      lines.push(`<div class="diag-line"><span>Poller status</span><span class="v ${hCls}">${(diag.pollerHealth||'unknown').toUpperCase()}</span></div>`);
      lines.push(`<div class="diag-line"><span>Ultimo tick poller</span><span class="v">${lastTick}</span></div>`);
      for (const [name, ts] of Object.entries(diag.capabilityLastSeen||{})) {
        const txt = ts ? new Date(ts).toLocaleString() : 'mai visto';
        const cls = ts ? 'green' : 'gray';
        lines.push(`<div class="diag-line"><span>${name}</span><span class="v ${cls}">${txt}</span></div>`);
      }
      if (!diag.kqlAvailable) {
        lines.push(`<div class="diag-line"><span>KQL</span><span class="v yellow">non configurato (Dashboard:LogsWorkspaceId)</span></div>`);
      }
      for (const i of (diag.issues||[])) {
        lines.push(`<div class="diag-line"><span style="color:var(--red)">⚠</span><span class="v red">${escapeHtml(i)}</span></div>`);
      }
      el.innerHTML = lines.join('');
    }

    function renderStuckList(rows) {
      const el = document.getElementById('stuckList');
      if (!rows.length) { el.innerHTML = '<div class="empty">nessuna entry bloccata</div>'; return; }
      el.innerHTML = '';
      for (const r of rows) {
        const div = document.createElement('div');
        div.className = 'stuck-row';
        div.innerHTML = `
          <div>
            <div><code>${escapeHtml(r.intuneDeviceId)}</code></div>
            <div class="age">stuck da ${r.ageHours.toFixed(1)} h · corr ${escapeHtml(r.correlationId.substring(0,8))}…</div>
          </div>
          <button data-id="${escapeHtml(r.intuneDeviceId)}">Reset</button>`;
        div.querySelector('button').addEventListener('click', () => doReset(r.intuneDeviceId));
        el.appendChild(div);
      }
    }

    // ─── search / trace / device ────────────────────────────────────────────
    function escapeHtml(s) {
      return String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }
    function showTab(name) {
      for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t.dataset.tab === name);
      for (const p of document.querySelectorAll('.tabpane')) p.classList.toggle('active', p.id === 'tab-' + name);
    }
    function hideTraceEmpty() {
      const e = document.getElementById('traceEmpty'); if (e) e.hidden = true;
      const t = document.getElementById('tracePanelTimeline'); if (t) t.hidden = false;
      const l = document.getElementById('tracePanelLedger'); if (l) l.hidden = false;
    }
    for (const t of document.querySelectorAll('.tab')) t.addEventListener('click', () => showTab(t.dataset.tab));

    // Accetta sia GUID classico (8-4-4-4-12) sia formato compatto "N" (32 hex
    // senza trattini) che e' quello effettivamente generato dall'API server
    // tramite Guid.NewGuid().ToString("N").
    const GUID_RE = /^(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[0-9a-f]{32})$/i;

    document.getElementById('btnTrace').addEventListener('click', () => {
      const q = document.getElementById('searchInput').value.trim();
      if (!q) return;
      if (GUID_RE.test(q)) traceByCorr(q);
      else recentByDevice(q);
    });
    document.getElementById('btnDevice').addEventListener('click', () => {
      const q = document.getElementById('searchInput').value.trim();
      if (q) recentByDevice(q);
    });
    document.getElementById('searchInput').addEventListener('keydown', e => {
      if (e.key === 'Enter') document.getElementById('btnTrace').click();
    });

    async function traceByCorr(corr) {
      showTab('trace');
      hideTraceEmpty();
      document.getElementById('traceMeta').textContent = 'caricamento…';
      document.getElementById('traceReco').innerHTML = '';
      document.getElementById('timeline').innerHTML = '';
      document.getElementById('devicePanel').hidden = true;
      try {
        const r = await fetch('/api/cruscotto/trace?corr=' + encodeURIComponent(corr), { cache:'no-store', credentials:'same-origin' });
        if (!r.ok) throw new Error('HTTP ' + r.status + ' ' + (await r.text()).substring(0,200));
        renderTrace(await r.json());
      } catch (e) {
        document.getElementById('traceMeta').textContent = 'errore: ' + e.message;
      }
    }

    async function recentByDevice(q) {
      showTab('trace');
      hideTraceEmpty();
      document.getElementById('traceMeta').textContent = 'caricamento storico device…';
      document.getElementById('traceReco').innerHTML = '';
      document.getElementById('timeline').innerHTML = '';
      document.getElementById('traceLedger').innerHTML = '<tr><td class="empty">—</td></tr>';
      const panel = document.getElementById('devicePanel');
      panel.hidden = false;
      const body = document.getElementById('deviceRows');
      body.innerHTML = '<div class="empty">caricamento…</div>';
      try {
        const r = await fetch('/api/cruscotto/device?q=' + encodeURIComponent(q), { cache:'no-store', credentials:'same-origin' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const data = await r.json();
        document.getElementById('traceMeta').textContent = `Device: ${data.device} — ${(data.rows||[]).length} richieste recenti`;
        if (!data.rows || data.rows.length === 0) {
          body.innerHTML = '<div class="empty">nessuna richiesta recente trovata in App Insights</div>';
          return;
        }
        body.innerHTML = '';
        for (const row of data.rows) {
          const div = document.createElement('div');
          div.className = 'row';
          div.innerHTML = `
            <div><code>${escapeHtml(row.correlationId.substring(0,13))}…</code></div>
            <div>${escapeHtml(row.lastEvent)} <span style="color:var(--muted)"> · ${new Date(row.lastEventAt).toLocaleString()}</span></div>
            <div style="color:var(--accent)">Trace ›</div>`;
          div.addEventListener('click', () => traceByCorr(row.correlationId));
          body.appendChild(div);
        }
      } catch (e) {
        document.getElementById('traceMeta').textContent = 'errore: ' + e.message;
        body.innerHTML = '';
      }
    }

    function classifyEvent(name) {
      const n = (name||'').toLowerCase();
      if (n.endsWith('.completed')) return 'completed';
      if (n.endsWith('.failed') || n.includes('exception')) return 'failed';
      if (n.includes('already-issued') || n.includes('dedup')) return 'dedup';
      if (n.includes('rearmed')) return 'rearmed';
      if (n.includes('accepted')) return 'accepted';
      if (n.includes('enqueued')) return 'enqueued';
      if (n.includes('received')) return 'received';
      if (n.includes('forwarded')) return 'forwarded';
      if (n.includes('consumed')) return 'consumed';
      return '';
    }

    function renderTrace(t) {
      document.getElementById('devicePanel').hidden = true;
      const meta = `Correlation: ${escapeHtml(t.correlationId)} · Device: ${escapeHtml(t.deviceName||'—')} · Intune: ${escapeHtml(t.intuneDeviceId||'—')} · ${t.events.length} eventi`;
      document.getElementById('traceMeta').textContent = meta;

      const rawEvents = Array.isArray(t.events) ? t.events : [];
      const checkpointDefs = [
        { id:'validated', label:'Validata (request received)', match:e => e.name === 'action.request.received' },
        { id:'accepted', label:'Accettata', match:e => e.name === 'action.request.accepted' },
        { id:'dispatch', label:'Presa in carico da Proc', match:e => e.name === 'action.dispatch.received' },
        { id:'forwarded', label:'Forward verso capability', match:e => e.name === 'action.forwarded' },
        { id:'consumed', label:'Consumata dal runner wipe', match:e => (e.name||'').endsWith('.action.consumed') },
        { id:'graph', label:'Richiesta Graph inviata', match:e => (e.name||'').includes('.graph.issued') },
        { id:'fallback', label:'Fallback attivato (sync/reboot)', match:e => (e.name||'').includes('fallback.issued') },
        { id:'runnerdone', label:'Runner completato', match:e => (e.name||'').endsWith('.action.completed') },
        { id:'terminal', label:'Terminale osservato dal poller', match:e => e.name === 'action.completed' }
      ];
      const cpRows = checkpointDefs.map(cp => {
        const ev = rawEvents.find(cp.match);
        return {
          label: cp.label,
          ok: !!ev,
          at: ev ? new Date(ev.timestamp).toLocaleString() : '—'
        };
      });
      const stateObservedCount = rawEvents.filter(e => e.name === 'action.state-observed').length;
      const cpHost = document.getElementById('traceCheckpoints');
      cpHost.className = '';
      cpHost.innerHTML = `
        <table class="kv">
          <tr><th>Checkpoint</th><th>Stato</th><th>Timestamp</th></tr>
          ${cpRows.map(r => `<tr><td>${escapeHtml(r.label)}</td><td>${r.ok ? '✅' : '—'}</td><td>${escapeHtml(r.at)}</td></tr>`).join('')}
          <tr><th>state-observed (poller)</th><td colspan="2">${stateObservedCount}</td></tr>
        </table>`;

      // Recommendation banner
      const rec = t.recommendation || { severity:'muted', title:'—', detail:'—', actionKind:'none' };
      const rDiv = document.createElement('div');
      rDiv.className = 'reco ' + (rec.severity || 'muted');
      let actionHtml = '';
      if (rec.actionKind === 'reset-ledger' && t.intuneDeviceId) {
        actionHtml = `<button class="danger" data-id="${escapeHtml(t.intuneDeviceId)}">🔧 Reset ledger e riprova</button>`;
      } else if (rec.actionKind === 'open-app-insights') {
        actionHtml = `<button data-corr="${escapeHtml(rec.actionPayload || t.correlationId)}">Apri in App Insights</button>`;
      } else if (rec.actionKind === 'open-azure-portal') {
        actionHtml = `<button data-portal="${escapeHtml(rec.actionPayload || '')}">Apri portale Azure</button>`;
      }
      rDiv.innerHTML = `<h3>${escapeHtml(rec.title)}</h3><p>${escapeHtml(rec.detail)}</p>${actionHtml}`;
      const btn = rDiv.querySelector('button');
      if (btn) {
        if (btn.dataset.id) btn.addEventListener('click', () => doReset(btn.dataset.id));
        else if (btn.dataset.corr) btn.addEventListener('click', () => alert('Cerca questo correlationId in App Insights:\n' + btn.dataset.corr));
        else if (btn.dataset.portal !== undefined) btn.addEventListener('click', () => alert('Apri il portale Azure e cerca:\n' + btn.dataset.portal));
      }
      const recHost = document.getElementById('traceReco');
      recHost.innerHTML = '';
      recHost.appendChild(rDiv);

      // Timeline
      const tl = document.getElementById('timeline');
      tl.innerHTML = '';
      const observed = rawEvents.filter(e => e.name === 'action.state-observed');
      const nonObserved = rawEvents.filter(e => e.name !== 'action.state-observed');
      let eventsToRender = nonObserved.slice();
      if (observed.length === 1) {
        eventsToRender.push(observed[0]);
      } else if (observed.length > 1) {
        eventsToRender.push({ ...observed[0], name: `action.state-observed (first of ${observed.length})` });
        eventsToRender.push({ ...observed[observed.length - 1], name: `action.state-observed (last of ${observed.length})` });
      }
      eventsToRender.sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime());
      for (const e of eventsToRender) {
        const div = document.createElement('div');
        div.className = 'tev ' + classifyEvent(e.name);
        const extras = [];
        if (e.deviceName) extras.push('dev=' + e.deviceName);
        if (e.intuneDeviceId) extras.push('intune=' + e.intuneDeviceId.substring(0,8) + '…');
        if (e.reason) extras.push('reason=' + e.reason);
        if (e.state) extras.push('state=' + e.state);
        if (e.terminalState) extras.push('terminal=' + e.terminalState);
        if (e.rawStatus) extras.push('raw=' + e.rawStatus);
        if (e.rearmReason) extras.push('rearm=' + e.rearmReason);
        if (e.originalCorrelationId) extras.push('orig=' + e.originalCorrelationId.substring(0,8) + '…');
        div.innerHTML = `
          <div class="ts">${new Date(e.timestamp).toLocaleTimeString()}<br><span style="font-size:10px">${new Date(e.timestamp).toLocaleDateString()}</span></div>
          <div class="role">${escapeHtml(e.role||'?')}</div>
          <div class="body">${escapeHtml(e.name)}${extras.length ? '<span class="small">' + escapeHtml(extras.join(' · ')) + '</span>' : ''}</div>`;
        tl.appendChild(div);
      }
      if (rawEvents.length === 0) {
        tl.innerHTML = '<div class="empty">nessun evento trovato in App Insights. Verifica che Dashboard:LogsWorkspaceId sia configurato e che la Web UAMI abbia ruolo Log Analytics Reader.</div>';
      }

      // Ledger summary
      const tbl = document.getElementById('traceLedger');
      const ls = t.ledgerSummary;
      if (!ls) {
        tbl.innerHTML = '<tr><td class="empty">nessuna entry ledger associata a questo device</td></tr>';
      } else {
        tbl.innerHTML = `
          <tr><th>State</th><td>${escapeHtml(ls.state||'—')}</td></tr>
          <tr><th>IssuedAt</th><td>${ls.issuedAt ? new Date(ls.issuedAt).toLocaleString() : '—'}</td></tr>
          <tr><th>LastTerminalState</th><td>${escapeHtml(ls.lastTerminalState||'—')}</td></tr>
          <tr><th>LastRearmedAt</th><td>${ls.lastRearmedAt ? new Date(ls.lastRearmedAt).toLocaleString() : '—'}</td></tr>
          <tr><th>CorrelationId (corrente)</th><td><code>${escapeHtml(ls.correlationId||'—')}</code></td></tr>
          <tr><th>ActionSequence</th><td>${ls.actionSequence ?? 0}</td></tr>`;
      }
    }

    async function doReset(intuneDeviceId) {
      const reason = prompt(`Reset ledger per device ${intuneDeviceId}?\n\nQuesta operazione archivia l'entry corrente — la prossima richiesta avrà un nuovo correlationId e raggiungerà Graph.\n\nMotivo (obbligatorio):`);
      if (!reason || !reason.trim()) return;
      try {
        const r = await fetch('/api/cruscotto/actions/reset-ledger', {
          method:'POST',
          headers:{'Content-Type':'application/json'},
          credentials:'same-origin',
          body: JSON.stringify({ intuneDeviceId, reason: reason.trim() }),
        });
        const body = await r.json().catch(() => ({}));
        if (!r.ok) {
          alert('Reset fallito (HTTP ' + r.status + '): ' + (body.message || 'errore sconosciuto'));
          return;
        }
        alert('Reset OK. Archiviato come:\n' + (body.archive||'?') + '\n\nOra puoi rilanciare la richiesta dal client.');
        tick();
      } catch (e) {
        alert('Reset fallito: ' + e.message);
      }
    }

    async function tick() {
      try {
        const r = await fetch('/api/cruscotto/data', { cache:'no-store', credentials:'same-origin' });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        render(await r.json());
      } catch (e) {
        setText('lastUpdate', 'errore: ' + e.message);
        document.getElementById('liveDot').style.background = 'var(--red)';
      }
    }
    tick();
    setInterval(tick, REFRESH_MS);