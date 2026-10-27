'use strict';

/*
 * tg-ws-proxy — панель мониторинга и настройки (F-5).
 *
 * Ограничения, заложенные в требованиях: ни сборщика, ни CDN, ни внешних шрифтов.
 * Всё, что нужно, — этот файл, site.css и index.html.
 */

const POLL_MS = 2000;
const PEER_LIMIT = 50;
const THEME_KEY = 'tgws-proxy-theme';
const TOKEN_KEY = 'tgws-proxy-token';

const $ = (id) => document.getElementById(id);

const state = {
  timer: null,
  config: null,
  defaultsLoaded: false,
  token: sessionStorage.getItem(TOKEN_KEY) || '',
  peers: [],
  peerFilter: '',
  peerGroup: 'pair',
  // Токены доступа (F-8.8) и их агрегированный трафик (F-9.5).
  tokens: [],
  tokenUsage: new Map(),
  editingTokenId: null,
  status: null,
  chartKey: '',
  dcReport: null,
};

/* ============================ форматирование ============================ */

const BYTE_UNITS = ['Б', 'КиБ', 'МиБ', 'ГиБ', 'ТиБ', 'ПиБ'];

function fmtBytes(value) {
  let n = Number(value) || 0;
  // Дробные значения (скорости меньше 1 Б/с) округляем: иначе на оси графика
  // появляются подписи вида «0.3333333333333333 Б/с».
  if (n < 1024) return `${Number.isInteger(n) ? n : n.toFixed(1)} Б`;
  let unit = 0;
  while (n >= 1024 && unit < BYTE_UNITS.length - 1) {
    n /= 1024;
    unit += 1;
  }
  return `${n >= 100 ? n.toFixed(0) : n.toFixed(1)} ${BYTE_UNITS[unit]}`;
}

function fmtRate(value) {
  return `${fmtBytes(value)}/с`;
}

function fmtNum(value) {
  return (Number(value) || 0).toLocaleString('ru-RU');
}

function fmtDuration(totalSeconds) {
  const s = Math.max(0, Math.floor(Number(totalSeconds) || 0));
  const days = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  const seconds = s % 60;

  if (days > 0) return `${days} д ${hours} ч`;
  if (hours > 0) return `${hours} ч ${minutes} мин`;
  if (minutes > 0) return `${minutes} мин ${seconds} с`;
  return `${seconds} с`;
}

function fmtTime(value) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

function fmtClock(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function pct(part, whole) {
  return whole > 0 ? `${((100 * part) / whole).toFixed(1)}%` : '—';
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

/* ============================ тема ============================ */

const THEME_LABELS = { auto: 'авто', light: 'светлая', dark: 'тёмная' };
const THEMES = ['auto', 'light', 'dark'];

function applyTheme(theme) {
  const value = THEMES.includes(theme) ? theme : 'auto';
  document.documentElement.dataset.theme = value;
  try {
    localStorage.setItem(THEME_KEY, value);
  } catch {
    /* приватный режим — просто не запоминаем выбор */
  }
  $('theme-toggle').textContent = `Тема: ${THEME_LABELS[value]}`;
}

function initTheme() {
  let saved = 'auto';
  try {
    saved = localStorage.getItem(THEME_KEY) || 'auto';
  } catch {
    saved = 'auto';
  }
  applyTheme(saved);

  $('theme-toggle').addEventListener('click', () => {
    const current = document.documentElement.dataset.theme || 'auto';
    applyTheme(THEMES[(THEMES.indexOf(current) + 1) % THEMES.length]);
  });
}

/* ============================ сеть ============================ */

class AuthRequired extends Error {}

async function apiFetch(url, options = {}) {
  const headers = Object.assign({}, options.headers);
  if (state.token) headers.Authorization = `Bearer ${state.token}`;

  const response = await fetch(url, Object.assign({}, options, { headers, cache: 'no-store' }));

  if (response.status === 401) {
    throw new AuthRequired('unauthorized');
  }
  return response;
}

async function apiJson(url, options) {
  const response = await apiFetch(url, options);
  if (!response.ok) {
    // Тело ProblemDetails сохраняем: панель различает 409 startupOnly / rolledBack /
    // «перезапуск уже идёт» (F-2.12, F-7.5, F-7.6).
    let problem = null;
    try {
      problem = await response.json();
    } catch {
      /* тело не JSON — оставляем статус */
    }

    const detail = (problem && (problem.detail || problem.title)) || `${response.status} ${response.statusText}`;
    const error = new Error(detail);
    error.status = response.status;
    error.problem = problem;
    throw error;
  }
  return response.json();
}

/* ============================ баннер ============================ */

let tokenPromptShown = false;

function showBanner(message, level = 'warn') {
  const banner = $('banner');
  banner.textContent = '';
  banner.dataset.level = level;
  banner.hidden = false;

  banner.append(el('span', '', message));

  if (level === 'auth' || level === 'error-auth') {
    // Если включён webApiToken, странице нужен Bearer-токен для /api/* (F-3.8).
    const form = el('form', 'input-row');
    const input = el('input');
    input.type = 'password';
    input.placeholder = 'Bearer-токен';
    input.autocomplete = 'off';
    input.setAttribute('aria-label', 'Bearer-токен для Web API');
    const submit = el('button', 'btn', 'Войти');
    submit.type = 'submit';
    form.append(input, submit);
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      const value = input.value.trim();
      if (!value) return;
      state.token = value;
      sessionStorage.setItem(TOKEN_KEY, value);
      tokenPromptShown = false;
      hideBanner();
      refresh();
      loadConfig();
    });
    banner.append(form);
    banner.dataset.level = 'warn';
    setTimeout(() => input.focus(), 0);
  }
}

function hideBanner() {
  const banner = $('banner');
  banner.hidden = true;
  banner.textContent = '';
}

function setStatus(stateName, text) {
  const pill = $('status-pill');
  pill.dataset.state = stateName;
  pill.textContent = text;

  if (state.status !== stateName) {
    state.status = stateName;
    $('live').textContent = text;
  }
}

/* ============================ обзор ============================ */

/**
 * Состояние прокси-листенера из /api/info (F-7.8): listening / restarting / unavailable.
 * Нужно, чтобы панель показывала перезапуск привязки отдельно от падения процесса (F-5.18).
 */
const LISTENER_STATES = {
  listening: { label: 'слушает', note: 'приём соединений идёт' },
  restarting: { label: 'перезапуск', note: 'привязка перенастраивается' },
  unavailable: { label: 'остановлен', note: 'порт не открыт' },
};

function listenerLabel(info) {
  const key = info && info.listenerState;
  return (LISTENER_STATES[key] || { label: '—' }).label;
}

function listenerNote(info) {
  if (!info) return 'нет данных';
  const restarts = info.listenerRestarts || 0;
  const parts = [];
  parts.push((LISTENER_STATES[info.listenerState] || { note: 'состояние неизвестно' }).note);
  parts.push(restarts > 0 ? `перезапусков ${fmtNum(restarts)}` : 'без перезапусков');
  if (restarts > 0 && info.lastListenerRestart) parts.push(`последний ${fmtTime(info.lastListenerRestart)}`);
  return parts.join(' · ');
}

function renderOverview(metrics, info) {
  const cards = $('overview-cards');
  cards.textContent = '';

  const rejected =
    (metrics.connectionsBlocked || 0) + (metrics.connectionsLimited || 0) + (metrics.connectionsRateLimited || 0);
  const errors = (metrics.wsErrors || 0) + (metrics.connectionsBad || 0);
  const total = metrics.connectionsTotal || 0;

  const items = [
    { label: 'Время работы', value: fmtDuration(metrics.uptimeSeconds), note: `с ${fmtTime(info && info.startedAt)}` },
    { label: 'Активные', value: fmtNum(metrics.connectionsActive), note: `из ${fmtNum(total)} всего`, accent: true },
    { label: 'Отправлено', value: fmtBytes(metrics.bytesUp), note: 'к Telegram' },
    { label: 'Получено', value: fmtBytes(metrics.bytesDown), note: 'от Telegram' },
    { label: 'Отклонено', value: fmtNum(rejected), note: `ACL ${fmtNum(metrics.connectionsBlocked)}, лимит ${fmtNum(metrics.connectionsLimited)}`, bad: rejected > 0 },
    { label: 'Ошибки', value: fmtNum(errors), note: `WS ${fmtNum(metrics.wsErrors)}, bad ${fmtNum(metrics.connectionsBad)}`, bad: errors > 0 },
    { label: 'Маршруты', value: fmtNum(metrics.connectionsWs), note: `WS ${fmtNum(metrics.connectionsWs)}, H2 ${fmtNum(metrics.connectionsH2)}, TCP ${fmtNum(metrics.connectionsTcpFallback)}` },
    { label: 'Слушатель', value: listenerLabel(info), note: listenerNote(info), bad: !!info && info.listenerState === 'unavailable' },
    { label: 'Отслежено IP', value: fmtNum(metrics.trackedPeers), note: metrics.peerTrackingOverflow > 0 ? `переполнение ${fmtNum(metrics.peerTrackingOverflow)}` : 'в пределах лимита', bad: metrics.peerTrackingOverflow > 0 },
  ];

  for (const item of items) {
    const card = el('div', 'card');
    if (item.accent) card.classList.add('card--accent');
    if (item.bad) card.classList.add('card--bad');
    card.append(el('span', 'card__label', item.label), el('span', 'card__value', item.value), el('span', 'card__note', item.note));
    cards.append(card);
  }

  $('version').textContent = metrics.version || (info && info.version) || '—';
}

/* ============================ график ============================ */

const SVG_NS = 'http://www.w3.org/2000/svg';

function svgEl(name, attrs) {
  const node = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attrs || {})) {
    node.setAttribute(key, String(value));
  }
  return node;
}

function renderTraffic(series) {
  const host = $('traffic-chart');
  const points = (series && series.points) || [];

  const key = `${points.length}:${points.length ? points[points.length - 1].timestamp : ''}`;
  const container = host;

  if (points.length < 2) {
    if (state.chartKey !== key) {
      container.textContent = '';
      container.append(el('div', 'chart__empty', 'Недостаточно данных — подождите несколько секунд'));
      state.chartKey = key;
    }
    return;
  }

  state.chartKey = key;
  container.textContent = '';

  const width = 900;
  const height = 190;
  const padLeft = 46;
  const padRight = 10;
  const padTop = 12;
  const padBottom = 22;

  const up = points.map((p) => p.bytesUpPerSecond || 0);
  const down = points.map((p) => p.bytesDownPerSecond || 0);
  const peak = Math.max(1, ...up, ...down);

  const x = (index) => padLeft + ((width - padLeft - padRight) * index) / (points.length - 1);
  const y = (value) => padTop + (height - padTop - padBottom) * (1 - value / peak);

  const svg = svgEl('svg', {
    viewBox: `0 0 ${width} ${height}`,
    preserveAspectRatio: 'none',
    role: 'img',
    'aria-label': `Скорость трафика: ${points.length} точек, пик ${fmtRate(peak)}`,
  });

  for (let i = 0; i <= 3; i += 1) {
    const value = (peak * i) / 3;
    const lineY = y(value);
    svg.append(svgEl('line', { x1: padLeft, x2: width - padRight, y1: lineY, y2: lineY, class: 'chart__grid' }));
    const label = svgEl('text', { x: 2, y: lineY + 3, class: 'chart__axis' });
    label.textContent = fmtRate(value);
    svg.append(label);
  }

  const line = (values) =>
    values.map((value, index) => `${index === 0 ? 'M' : 'L'}${x(index).toFixed(1)},${y(value).toFixed(1)}`).join(' ');

  const area = svgEl('path', {
    d: `${line(up)} L${x(up.length - 1).toFixed(1)},${height - padBottom} L${x(0).toFixed(1)},${height - padBottom} Z`,
    class: 'chart__area',
    fill: 'var(--up)',
  });
  svg.append(area);

  svg.append(svgEl('path', { d: line(down), class: 'chart__line-down' }));
  svg.append(svgEl('path', { d: line(up), class: 'chart__line-up' }));

  const first = svgEl('text', { x: padLeft, y: height - 6, class: 'chart__axis' });
  first.textContent = fmtClock(points[0].timestamp);
  const last = svgEl('text', { x: width - padRight, y: height - 6, class: 'chart__axis', 'text-anchor': 'end' });
  last.textContent = fmtClock(points[points.length - 1].timestamp);
  svg.append(first, last);

  container.append(svg);
}

/* ============================ типы соединений ============================ */

function renderTypes(metrics) {
  const host = $('type-bars');
  host.textContent = '';

  const total = metrics.connectionsTotal || 0;
  const rows = [
    ['WebSocket', metrics.connectionsWs || 0, 'var(--accent)'],
    ['HTTP/2', metrics.connectionsH2 || 0, 'var(--down)'],
    ['TCP fallback', metrics.connectionsTcpFallback || 0, 'var(--ok)'],
    ['CF proxy', metrics.connectionsCfProxy || 0, 'var(--warn)'],
    ['Fronting', metrics.connectionsFronting || 0, 'var(--muted)'],
    ['Маскировка', metrics.connectionsMasked || 0, 'var(--muted)'],
    ['Отклонено', (metrics.connectionsBlocked || 0) + (metrics.connectionsLimited || 0) + (metrics.connectionsRateLimited || 0), 'var(--bad)'],
  ];

  for (const [label, value, color] of rows) {
    const row = el('li', 'bars__row');
    const track = el('div', 'bars__track');
    const fill = el('span', 'bars__fill');
    fill.style.width = `${total > 0 ? Math.min(100, (100 * value) / total) : 0}%`;
    fill.style.background = color;
    track.append(fill);

    const caption = el('span', 'bars__value', `${fmtNum(value)}`);
    caption.title = `${label}: ${fmtNum(value)} (${pct(value, total)})`;

    row.append(el('span', 'bars__label', label), track, caption);
    host.append(row);
  }
}

/* ============================ пул ============================ */

function renderPool(metrics) {
  const host = $('pool-cards');
  host.textContent = '';

  const poolTotal = (metrics.poolHits || 0) + (metrics.poolMisses || 0);
  const cfTotal = (metrics.cfPoolHits || 0) + (metrics.cfPoolMisses || 0);

  const items = [
    ['WS попадания', fmtNum(metrics.poolHits), `промахов ${fmtNum(metrics.poolMisses)}`],
    ['WS hit rate', pct(metrics.poolHits || 0, poolTotal), `${fmtNum(poolTotal)} обращений`],
    ['CF попадания', fmtNum(metrics.cfPoolHits), `промахов ${fmtNum(metrics.cfPoolMisses)}`],
    ['CF hit rate', pct(metrics.cfPoolHits || 0, cfTotal), `${fmtNum(cfTotal)} обращений`],
    ['H2 запросы', fmtNum(metrics.h2Requests), `TCP ${fmtNum(metrics.h2TcpConnections)}, ошибок ${fmtNum(metrics.h2Errors)}`],
  ];

  for (const [label, value, note] of items) {
    const card = el('div', 'card');
    card.append(el('span', 'card__label', label), el('span', 'card__value', value), el('span', 'card__note', note));
    host.append(card);
  }
}

/* ============================ таблица по IP ============================ */

function renderPeers() {
  const body = $('peer-rows');
  body.textContent = '';

  const peers = state.peers;
  if (!peers.length) {
    const row = el('tr');
    const cell = el('td', 'table__empty', state.peerFilter ? 'Нет источников, подходящих под фильтр' : 'Статистика по источникам пока пуста');
    cell.colSpan = 9;
    row.append(cell);
    body.append(row);
    $('peer-summary').textContent = '';
    return;
  }

  const peak = Math.max(1, ...peers.map((peer) => (peer.bytesUp || 0) + (peer.bytesDown || 0)));

  for (const peer of peers) {
    const row = el('tr');

    const ipCell = el('td');
    // Группировка задаёт, что стоит в первой колонке (F-9.4): пара «IP + токен»,
    // только IP или только токен.
    const label = state.peerGroup === 'token'
      ? (peer.tokenName || peer.tokenId || 'без токена')
      : (peer.ip || peer.tokenName || '—');
    ipCell.append(el('span', 'table__ip', label));

    if (state.peerGroup === 'pair') {
      ipCell.append(el('span', 'table__token', peer.tokenName || 'без токена'));
    }

    const bar = el('span', 'table__bar');
    const barFill = el('span');
    barFill.style.width = `${Math.min(100, (100 * ((peer.bytesUp || 0) + (peer.bytesDown || 0))) / peak)}%`;
    bar.append(barFill);
    ipCell.append(bar);

    const pool = `${fmtNum(peer.poolHits)} / ${fmtNum(peer.poolMisses)}`;

    row.append(
      ipCell,
      el('td', 'num', fmtNum(peer.activeConnections)),
      el('td', 'num', fmtNum(peer.totalConnections)),
      el('td', 'num', fmtBytes(peer.bytesUp)),
      el('td', 'num', fmtBytes(peer.bytesDown)),
      el('td', 'num', fmtNum(peer.rejected)),
      el('td', 'num', fmtNum(peer.wsErrors)),
      el('td', 'num', pool),
      el('td', '', fmtTime(peer.lastSeen)),
    );

    body.append(row);
  }

  $('peer-summary').textContent = `Показано ${peers.length} из ${state.peerTotal ?? peers.length} источников · отсортировано по трафику`;
}

function renderPeersSafe() {
  const body = $('peer-rows');
  if (!state.peers.length) {
    renderPeers();
    return;
  }
  // Перерисовка целиком: 50 строк — не тот объём, ради которого стоит городить diff.
  renderPeers();
  void body;
}

/* ============================ опрос ============================ */

async function refresh() {
  try {
    const peersUrl = `/api/per-ip?limit=${PEER_LIMIT}&group=${encodeURIComponent(state.peerGroup)}`
      + (state.peerFilter ? `&q=${encodeURIComponent(state.peerFilter)}` : '');

    const [health, metrics, info, timeseries, peers, tokens, usage] = await Promise.all([
      apiFetch('/healthz').catch(() => null),
      apiJson('/api/metrics'),
      apiJson('/api/info').catch(() => null),
      apiJson('/api/metrics/timeseries').catch(() => null),
      apiJson(peersUrl).catch(() => null),
      apiJson('/api/tokens').catch(() => null),
      apiJson('/api/metrics/by-token').catch(() => null),
    ]);

    const healthy = health ? health.ok : false;
    // Перезапуск слушателя — не падение: процесс жив, привязка перенастраивается (F-7.7).
    const listenerState = info ? info.listenerState : null;

    if (listenerState === 'restarting') {
      if (state.status !== 'restarting') {
        showBanner('Слушатель перезапускается: активные сессии разорваны, Web API продолжает работу.', 'warn');
      }
      setStatus('restarting', 'перезапуск слушателя…');
      tokenPromptShown = false;
    } else if (healthy) {
      setStatus('ok', 'прокси работает');
      hideBanner();
      tokenPromptShown = false;
    } else {
      setStatus('down', 'прокси не слушает порт');
      showBanner('Прокси не принимает соединения: слушатель остановлен. Метрики могут быть устаревшими.', 'error');
    }

    // Состояние слушателя нужно для индикаторов; ссылки tg:// у токенов строятся из конфига (mtproto.publicHost).
    state.info = info;

    renderOverview(metrics, info);
    renderTraffic(timeseries);
    renderTypes(metrics);
    renderPool(metrics);

    // Нужно для подтверждения перезапуска слушателя: столько сессий будет разорвано (F-7.4).
    state.connectionsActive = metrics.connectionsActive ?? 0;

    if (peers) {
      state.peers = peers.items || [];
      state.peerTotal = peers.total ?? state.peers.length;
      renderPeers();
    }

    if (tokens) state.tokens = tokens;
    if (usage) state.tokenUsage = new Map(usage.map((item) => [item.tokenId, item]));

    if (tokens || usage) renderTokens();
  } catch (error) {
    if (error instanceof AuthRequired) {
      setStatus('down', 'требуется токен');
      if (!tokenPromptShown) {
        tokenPromptShown = true;
        showBanner('Web API защищён токеном: введите Bearer-токен, чтобы видеть данные.', 'auth');
      }
      return;
    }

    setStatus('down', 'нет связи с Web API');
    showBanner(`Не удалось получить данные: ${error.message}. Повтор через ${POLL_MS / 1000} с.`, 'error');
  }
}

function startPolling() {
  stopPolling();
  state.timer = setInterval(() => {
    // Скрытая вкладка не опрашивает сервер (F-5.3).
    if (document.visibilityState === 'visible') refresh();
  }, POLL_MS);
}

function stopPolling() {
  if (state.timer !== null) {
    clearInterval(state.timer);
    state.timer = null;
  }
}

/* ============================ форма конфигурации ============================ */

const CONFIG_GROUPS = [
  {
    // Блок справочный: Swagger меняется только в config.json,
    // API отклоняет его правку с 409 (F-2.12, F-5.16).
    // Адрес и порт Web API фиксированы (0.0.0.0:80) и не настраиваются.
    legend: 'Web API',
    info: true,
    fields: [
      { key: 'enableSwagger', label: 'Swagger (/swagger)', type: 'bool', startup: true, hint: 'Маршруты регистрируются при старте' },
    ],
  },
  {
    // Блок справочный: привязка слушателей и параметры WEB proxy задаются в config.json
    // (стартовые ключи, API отклоняет их правку с 409). Разбит на подразделы MTPROTO
    // (основной proxy-слушатель) и WebProxy (browser-relay).
    legend: 'Сеть',
    info: true,
    sections: [
      {
        title: 'MTPROTO',
        fields: [
          { section: 'mtproto', key: 'port', label: 'Порт прокси', type: 'number', startup: true },
        ],
      },
      {
        title: 'WebProxy',
        fields: [
          { section: 'webProxy', key: 'enabled', label: 'WEB proxy включён', type: 'bool' },
          { section: 'webProxy', key: 'port', label: 'Порт bridge', type: 'number', startup: true },
          { section: 'webProxy', key: 'basePath', label: 'Base path', type: 'text', startup: true },
          { section: 'webProxy', key: 'publicHost', label: 'Публичный домен', type: 'text', hint: 'Домен для ссылок t.me/webproxy (IP не принимается)' },
          { section: 'webProxy', key: 'carrierMode', label: 'Носитель', type: 'text', hint: 'https (long-poll) или websocket' },
          { section: 'webProxy', key: 'certPath', label: 'Сертификат (PFX)', type: 'text', startup: true },
          { section: 'webProxy', key: 'maxSessions', label: 'Максимум сессий', type: 'number' },
          { section: 'webProxy', key: 'maxSessionsPerIp', label: 'Сессий на IP', type: 'number', hint: '0 — без ограничения' },
          { section: 'webProxy', key: 'maxStreamsPerSession', label: 'Потоков на сессию', type: 'number' },
          { section: 'webProxy', key: 'maxStreams', label: 'Потоков всего', type: 'number' },
        ],
      },
    ],
  },
  {
    // Блок справочный: уровень журнала и файловые настройки читаются при запуске (F-5.16).
    legend: 'Журналирование',
    info: true,
    fields: [
      { key: 'verbose', label: 'Подробный лог (verbose)', type: 'bool', startup: true },
      { section: 'logs', key: 'path', label: 'Файл журнала', type: 'text', startup: true, hint: 'Пусто — только консоль' },
      { section: 'logs', key: 'maxMegabytes', label: 'Размер файла, МБ', type: 'number', min: 0, max: 4096, step: 0.1, startup: true, hint: '0 — ротация по часам' },
      { section: 'logs', key: 'retainedFileCount', label: 'Хранить файлов', type: 'number', min: 1, max: 1000, startup: true },
    ],
  },
  {
    // Внешний адрес нужен только для ссылок t.me/proxy: слушатель всегда привязан ко всем
    // интерфейсам (0.0.0.0), и по нему клиент не подключится. Меняется на лету.
    legend: 'Ссылки Telegram',
    fields: [
      {
        section: 'mtproto',
        key: 'publicHost',
        label: 'Внешний адрес прокси',
        type: 'text',
        hint: 'Адрес и порт, по которым клиенты подключаются снаружи: proxy.example.com, proxy.example.com:1443 или [2001:db8::1]:1443. Порт можно не указывать — возьмётся порт прокси. Пусто — ссылка не строится (слушатель всегда привязан ко всем интерфейсам 0.0.0.0)',
      },
    ],
  },
  {
    legend: 'Безопасность',
    fields: [
      // Токены прокси больше не редактируются здесь: у них своя панель (F-8.8), а в
      // config.json они живут списком tokens[], а не ключом secret.
      { key: 'webApiToken', label: 'Bearer-токен Web API', type: 'secret', hint: 'Пусто — /api/* без аутентификации; меняется на лету' },
      { key: 'allowIp', label: 'Разрешённые IP / CIDR', type: 'list', hint: 'Список адресов: по одному в строке (можно через запятую или точку с запятой). Одиночный адрес — 203.0.113.7, подсеть — 198.51.100.0/24, IPv6 — 2001:db8::/32. Пусто — разрешены все адреса' },
      { key: 'denyIp', label: 'Запрещённые IP / CIDR', type: 'list', hint: 'Тот же формат: адрес или подсеть, по одному в строке. Запрет проверяется первым и важнее allowIp, поэтому адрес из обоих списков будет отклонён' },
      { key: 'disableSecure', label: 'Отключить Secure (fake-TLS)', type: 'bool', listener: true },
      { section: 'mtproto', key: 'allowInvalidCertificates', label: 'Принимать недействительные сертификаты', type: 'bool', listener: true },
      { key: 'fakeTlsDomain', label: 'Домен fake-TLS', type: 'text', listener: true },
      { key: 'proxyProtocol', label: 'PROXY protocol', type: 'bool', listener: true },
      { key: 'forceTestDc', label: 'Тестовые DC', type: 'bool', listener: true },
    ],
  },
  {
    legend: 'DC и пул WebSocket',
    fields: [
      { section: 'mtproto', key: 'dcIp', label: 'DC → IP', type: 'list', listener: true, hint: 'Формат DC:IP, по одному в строке; пусто — пул WS отключён' },
      { section: 'mtproto', key: 'wsPoolSize', label: 'Размер пула WS', type: 'number', min: 0, max: 64, listener: true },
      { section: 'mtproto', key: 'wsMaxFrameBytes', label: 'Максимальный кадр WS, байт', type: 'number', min: 1024, max: 67108864, listener: true },
      { section: 'mtproto', key: 'wsConnectTimeoutSeconds', label: 'Таймаут подключения WS, с', type: 'number', min: 1, max: 300, listener: true },
      { section: 'mtproto', key: 'socketBufferKb', label: 'Буфер сокета, КБ', type: 'number', min: 4, max: 8192, listener: true },
    ],
  },
  {
    legend: 'Ограничения',
    fields: [
      { key: 'maxConnections', label: 'Максимум соединений', type: 'number', min: 0, max: 1000000, hint: '0 — без ограничения' },
      { key: 'rateLimitPerIp', label: 'Лимит на IP', type: 'number', min: 0, max: 1000000, hint: '0 — без ограничения' },
      { key: 'rateLimitWindowSeconds', label: 'Окно лимита, с', type: 'number', min: 1, max: 86400 },
    ],
  },
  {
    legend: 'Cloudflare',
    fields: [
      { section: 'cfProxy', key: 'enabled', label: 'CF proxy включён', type: 'bool', listener: true },
      { section: 'cfProxy', key: 'h2Media', label: 'CF HTTP/2 для медиа', type: 'bool', listener: true },
      { section: 'cfProxy', key: 'domains', label: 'Домены CF proxy', type: 'list', listener: true, hint: 'По одному в строке' },
      { section: 'cfProxy', key: 'workerDomains', label: 'Домены CF worker', type: 'list', listener: true, hint: 'По одному в строке' },
    ],
  },
  {
    legend: 'Мониторинг',
    fields: [
      { section: 'stats', key: 'enabled', label: 'Статистика по IP', type: 'bool' },
      { section: 'stats', key: 'ttlSeconds', label: 'TTL записи IP, с', type: 'number', min: 0, max: 604800, hint: '0 — не удалять' },
      { section: 'stats', key: 'maxPeers', label: 'Максимум отслеживаемых IP', type: 'number', min: 1, max: 10000000 },
      { key: 'prometheusTopPeers', label: 'Источников в /metrics', type: 'number', min: 0, max: 10000, hint: 'Сколько пар «IP + токен» отдавать в Prometheus; 0 — ни одной' },
      { key: 'timeseriesWindowSeconds', label: 'Окно графика, с', type: 'number', min: 60, max: 86400, hint: 'История графика сбрасывается' },
      { key: 'timeseriesStepSeconds', label: 'Шаг графика, с', type: 'number', min: 1, max: 3600, hint: 'История графика сбрасывается' },
    ],
  },
];

const IP_ENTRY = /^[0-9a-fA-F.:]+(\/\d{1,3})?$/;
const DC_ENTRY = /^\d{1,3}:\S+$/;
const SECRET = /^[0-9a-fA-F]{32}$/;

function fieldId(key) {
  return `cfg-${key}`;
}

/**
 * Полный путь поля в JSON-конфигурации: у вложенных блоков (otel/webProxy/cfProxy)
 * это «section.key», у плоских ключей — просто key.
 */
function fieldPath(field) {
  return field.section ? `${field.section}.${field.key}` : field.key;
}

/** Читает значение по пути «a.b.c», возвращая undefined при обрыве цепочки. */
function readPath(obj, path) {
  if (obj == null) return undefined;
  return String(path).split('.').reduce((acc, part) => (acc == null ? undefined : acc[part]), obj);
}

/** Записывает значение по пути «a.b.c», создавая промежуточные объекты. */
function writePath(obj, path, value) {
  const parts = String(path).split('.');
  let cursor = obj;
  for (let i = 0; i < parts.length - 1; i++) {
    const part = parts[i];
    if (cursor[part] == null || typeof cursor[part] !== 'object') cursor[part] = {};
    cursor = cursor[part];
  }
  cursor[parts[parts.length - 1]] = value;
}

/**
 * Группа конфигурации может быть плоской (fields) либо разбитой на подразделы
 * (sections). Возвращает единый плоский список полей для всех циклов обработки.
 */
function groupFields(group) {
  return group.sections ? group.sections.flatMap((s) => s.fields) : (group.fields || []);
}

function buildForm() {
  const host = $('config-groups');
  host.textContent = '';

  for (const group of CONFIG_GROUPS) {
    const box = el('fieldset', group.info ? 'field-group field-group--info' : 'field-group');
    box.append(el('legend', '', group.legend));

    if (group.sections) {
      for (const section of group.sections) {
        const sectionBox = el('div', 'field-section');
        sectionBox.append(el('div', 'field-section__title', section.title));
        for (const field of section.fields) {
          sectionBox.append(group.info ? buildInfoRow(field) : buildField(field));
        }
        box.append(sectionBox);
      }
    } else {
      for (const field of group.fields) {
        // Справочные блоки показываются текстом: править их можно только в config.json,
        // API отклоняет изменение таких ключей с 409 (F-2.12, F-5.16).
        box.append(group.info ? buildInfoRow(field) : buildField(field));
      }
    }

    host.append(box);
  }
}

/**
 * Строка справочного блока: метка и значение простым текстом, без поля ввода.
 */
function buildInfoRow(field) {
  const wrapper = el('div', 'field field--info');
  wrapper.dataset.key = fieldPath(field);
  wrapper.dataset.category = 'startup';

  wrapper.append(el('span', 'field__label', field.label));
  wrapper.append(el('span', 'field__value'));
  if (field.hint) wrapper.append(el('span', 'field__hint', field.hint));

  return wrapper;
}

/**
 * Значение для справочной строки: булевы ключи читаются как «включено/выключено»,
 * пустые — как прочерк, чтобы строка не выглядела незаполненной.
 */
function infoValue(field, config) {
  const raw = config ? readPath(config, fieldPath(field)) : undefined;

  if (field.type === 'bool') return raw ? 'включено' : 'выключено';
  if (field.type === 'list') return Array.isArray(raw) && raw.length > 0 ? raw.join(', ') : '—';
  if (raw === null || raw === undefined || String(raw).trim() === '') return '—';

  return String(raw);
}

function fieldCategory(field) {
  if (field.startup) return 'startup';
  if (field.listener) return 'listener';
  return 'hot';
}

function categoryBadge(field) {
  const category = fieldCategory(field);
  if (category === 'startup') {
    const badge = el('span', 'field__restart', 'только старт');
    badge.title = 'Ключ читается при запуске процесса: изменение через API отклоняется (409)';
    return badge;
  }
  if (category === 'listener') {
    const badge = el('span', 'field__restart', 'перезапуск слушателя');
    badge.title = 'При изменении слушатель перезапускается, активные сессии разрываются';
    // Скрыт по умолчанию: показывается только после реального изменения поля,
    // чтобы на свежей странице не выглядело как требование перезапуска.
    badge.hidden = true;
    return badge;
  }
  return null;
}

function buildField(field) {
  const id = fieldId(fieldPath(field));
  const wrapper = el('div', 'field');
  wrapper.dataset.key = fieldPath(field);

  const category = fieldCategory(field);
  wrapper.dataset.category = category;

  if (field.type === 'bool') {
    const label = el('label', 'switch');
    label.htmlFor = id;
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.id = id;
    if (category === 'startup') input.disabled = true;
    label.append(input, el('span', '', field.label));
    wrapper.append(label);
    const badge = categoryBadge(field);
    if (badge) wrapper.append(badge);
    if (field.hint) wrapper.append(el('span', 'field__hint', field.hint));
    return wrapper;
  }

  const label = el('label', 'field__label', field.label);
  label.htmlFor = id;
  const badge = categoryBadge(field);
  if (badge) label.append(badge);
  wrapper.append(label);

  if (field.type === 'list') {
    const area = document.createElement('textarea');
    area.id = id;
    area.rows = 3;
    area.spellcheck = false;
    wrapper.append(area);
  } else if (field.type === 'secret') {
    const row = el('div', 'input-row');
    const input = document.createElement('input');
    input.type = 'password';
    input.id = id;
    input.autocomplete = 'off';
    input.spellcheck = false;

    const toggle = el('button', 'btn', 'Показать');
    toggle.type = 'button';
    toggle.addEventListener('click', () => {
      const hidden = input.type === 'password';
      input.type = hidden ? 'text' : 'password';
      toggle.textContent = hidden ? 'Скрыть' : 'Показать';
    });

    const generate = el('button', 'btn', 'Сгенерировать');
    generate.type = 'button';
    generate.addEventListener('click', () => {
      const bytes = new Uint8Array(16);
      crypto.getRandomValues(bytes);
      input.value = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
      input.type = 'text';
      toggle.textContent = 'Скрыть';
    });

    row.append(input, toggle, generate);
    wrapper.append(row);
  } else {
    const input = document.createElement('input');
    input.type = field.type === 'number' ? 'number' : 'text';
    input.id = id;
    input.autocomplete = 'off';
    if (field.min !== undefined) input.min = String(field.min);
    if (field.max !== undefined) input.max = String(field.max);
    if (field.step !== undefined) input.step = String(field.step);
    if (field.type === 'text') input.spellcheck = false;
    wrapper.append(input);
  }

  if (category === 'startup') {
    // Ключи категории «только старт» в панели доступны лишь для чтения (F-5.13): сервер
    // отклонил бы их изменение с 409, поэтому даём увидеть значение, но не править его.
    wrapper.querySelectorAll('input, textarea').forEach((node) => {
      node.disabled = true;
    });
  }

  if (field.hint) wrapper.append(el('span', 'field__hint', field.hint));
  wrapper.append(el('span', 'field__error'));

  return wrapper;
}

function fieldValue(field, config) {
  const raw = config ? readPath(config, fieldPath(field)) : undefined;
  if (field.type === 'bool') return Boolean(raw);
  if (field.type === 'list') return Array.isArray(raw) ? raw.join('\n') : '';
  if (raw === null || raw === undefined) return '';
  return String(raw);
}

function fillForm(config) {
  state.config = config;

  for (const group of CONFIG_GROUPS) {
    for (const field of groupFields(group)) {
      const wrapper = document.querySelector(`.field[data-key="${fieldPath(field)}"]`);
      if (!wrapper) continue;

      if (group.info) {
        const value = wrapper.querySelector('.field__value');
        if (value) value.textContent = infoValue(field, config);
        continue;
      }

      const input = $(fieldId(fieldPath(field)));
      if (!input) continue;

      wrapper.classList.remove('field--invalid');
      if (field.type === 'bool') input.checked = fieldValue(field, config);
      else input.value = fieldValue(field, config);
    }
  }

  $('config-path').textContent = '';

  // Сбрасываем пометки: после загрузки/сохранения/сброса изменений ещё нет.
  refreshCategoryBadges();
}

function collectForm() {
  const payload = {};

  for (const group of CONFIG_GROUPS) {
    // Справочные блоки не редактируются и в запрос не попадают: сервер трактует
    // отсутствующий ключ как «не менять» (F-2.11).
    if (group.info) continue;

    for (const field of group.fields) {
      const input = $(fieldId(fieldPath(field)));
      if (!input) continue;

      let value;
      if (field.type === 'bool') {
        value = input.checked;
      } else if (field.type === 'list') {
        value = input.value
          .split(/[\n,;]+/)
          .map((item) => item.trim())
          .filter((item) => item.length > 0);
      } else if (field.type === 'number') {
        value = input.value === '' ? null : Number(input.value);
      } else {
        const text = input.value.trim();
        // Маскированный секрет (abcd…ef01) не должен попасть в файл вместо настоящего.
        value = field.type === 'secret' && (text.includes('…') || text.includes('*')) ? null : text;
      }
      writePath(payload, fieldPath(field), value);
    }
  }

  return payload;
}

function validate(payload) {
  const errors = {};

  for (const group of CONFIG_GROUPS) {
    // Справочные значения не редактируются — проверять в них нечего.
    if (group.info) continue;

    for (const field of group.fields) {
      const value = readPath(payload, fieldPath(field));

      if (field.required && (value === null || value === undefined || String(value).trim() === '')) {
        errors[fieldPath(field)] = 'Обязательное поле';
        continue;
      }

      if (field.type === 'number' && value !== null && value !== undefined) {
        if (Number.isNaN(value)) {
          errors[fieldPath(field)] = 'Ожидается число';
        } else if (field.min !== undefined && value < field.min) {
          errors[fieldPath(field)] = `Минимум ${field.min}`;
        } else if (field.max !== undefined && value > field.max) {
          errors[fieldPath(field)] = `Максимум ${field.max}`;
        }
      }

      if (field.key === 'dcIp' && Array.isArray(value)) {
        const bad = value.find((item) => !DC_ENTRY.test(item));
        if (bad) errors[fieldPath(field)] = `Неверный формат «${bad}» — ожидается DC:IP`;
      }

      if ((field.key === 'allowIp' || field.key === 'denyIp') && Array.isArray(value)) {
        const bad = value.find((item) => !IP_ENTRY.test(item));
        if (bad) errors[fieldPath(field)] = `Неверный адрес «${bad}» — ожидается IP или CIDR`;
      }
    }
  }

  // Перекрёстная проверка: шаг не может быть больше окна.
  const windowSeconds = payload.timeseriesWindowSeconds;
  const stepSeconds = payload.timeseriesStepSeconds;
  if (typeof windowSeconds === 'number' && typeof stepSeconds === 'number' && stepSeconds > windowSeconds) {
    errors.timeseriesStepSeconds = 'Шаг не может превышать окно';
  }

  return errors;
}

function findField(key) {
  for (const group of CONFIG_GROUPS) {
    for (const field of groupFields(group)) {
      if (fieldPath(field) === key) return field;
    }
  }
  return null;
}

function sameValue(left, right) {
  return JSON.stringify(left ?? null) === JSON.stringify(right ?? null);
}

/**
 * Ключи, реально изменённые относительно загруженной конфигурации (§ 4).
 * Маскированные секреты (abcd…ef01) изменением не считаются: сервер их игнорирует.
 */
function changedKeys(payload) {
  const loaded = state.config || {};
  const keys = [];

  for (const group of CONFIG_GROUPS) {
    // Справочные блоки в payload не попадают — сверять их не с чем.
    if (group.info) continue;

    for (const field of groupFields(group)) {
      const path = fieldPath(field);
      const value = readPath(payload, path);
      if (value === undefined) continue;

      const before = readPath(loaded, path);
      const isMasked = field.type === 'secret'
        && typeof before === 'string'
        && (before.includes('…') || before.includes('*'));

      if (isMasked || sameValue(value, before)) continue;
      keys.push(path);
    }
  }

  return keys;
}

function listenerKeysAmong(keys) {
  return keys.filter((key) => {
    const field = findField(key);
    return field ? fieldCategory(field) === 'listener' : false;
  });
}

/**
 * Показывает бейдж «перезапуск слушателя» только у полей, реально изменённых
 * относительно загруженной конфигурации. На свежей странице ни одно поле не
 * помечается — пометка появляется по мере правки (F-5.13).
 */
function refreshCategoryBadges() {
  const changed = new Set(listenerKeysAmong(changedKeys(collectForm())));

  for (const group of CONFIG_GROUPS) {
    for (const field of groupFields(group)) {
      if (fieldCategory(field) !== 'listener') continue;
      const wrapper = document.querySelector(`.field[data-key="${fieldPath(field)}"]`);
      const badge = wrapper ? wrapper.querySelector('.field__restart') : null;
      if (badge) badge.hidden = !changed.has(fieldPath(field));
    }
  }
}

function showValidation(errors) {
  for (const group of CONFIG_GROUPS) {
    for (const field of groupFields(group)) {
      const wrapper = document.querySelector(`.field[data-key="${fieldPath(field)}"]`);
      if (!wrapper) continue;

      const message = errors[fieldPath(field)];
      wrapper.classList.toggle('field--invalid', Boolean(message));
      const hint = wrapper.querySelector('.field__error');
      if (hint) hint.textContent = message || '';

      const input = $(fieldId(fieldPath(field)));
      if (input) {
        if (message) input.setAttribute('aria-invalid', 'true');
        else input.removeAttribute('aria-invalid');
      }
    }
  }

  const firstKey = Object.keys(errors)[0];
  if (firstKey) {
    const input = $(fieldId(firstKey));
    if (input) input.focus();
  }
}

function setFormStatus(message, tone) {
  const status = $('config-status');
  status.textContent = message;
  status.dataset.tone = tone || '';
}

async function loadConfig() {
  try {
    const config = await apiJson('/api/config');
    fillForm(config);
    return config;
  } catch (error) {
    if (error instanceof AuthRequired) return null;
    setFormStatus(`Не удалось загрузить конфигурацию: ${error.message}`, 'bad');
    return null;
  }
}

async function saveConfig(event) {
  event.preventDefault();

  const payload = collectForm();
  const errors = validate(payload);
  showValidation(errors);

  if (Object.keys(errors).length > 0) {
    setFormStatus('Проверьте выделенные поля', 'bad');
    return;
  }

  const changed = changedKeys(payload);
  const listenerChanged = listenerKeysAmong(changed);

  // F-5.13: сохранение ключей «слушателя» требует подтверждения с числом активных сессий.
  if (listenerChanged.length > 0) {
    const active = state.connectionsActive || 0;
    const confirmed = window.confirm(
      `Изменяются ключи слушателя: ${listenerChanged.join(', ')}.\n` +
      `Слушатель будет перезапущен, активные сессии (${active}) оборвутся.\n\nПродолжить?`,
    );
    if (!confirmed) {
      setFormStatus('Сохранение отменено', 'warn');
      return;
    }
  }

  const button = $('config-save');
  button.disabled = true;
  setFormStatus(listenerChanged.length > 0 ? 'Применение: перезапуск слушателя…' : 'Сохранение…', '');

  try {
    const result = await apiJson('/api/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });

    const parts = [`Сохранено в ${result.path}`];
    if (result.hotReloaded && result.hotReloaded.length) parts.push(`применено сразу: ${result.hotReloaded.join(', ')}`);
    if (result.listenerRestarted) parts.push(`слушатель перезапущен, разорвано сессий: ${result.droppedSessions}`);
    if (result.warnings && result.warnings.length) parts.push(`замечания: ${result.warnings.join('; ')}`);

    // Новый Bearer-токен применяется сразу (hotReloaded), поэтому страница
    // должна переключиться на него, иначе следующий опрос получит 401.
    if (typeof payload.webApiToken === 'string' && payload.webApiToken.length > 0) {
      state.token = payload.webApiToken;
      sessionStorage.setItem(TOKEN_KEY, payload.webApiToken);
      parts.push('Bearer-токен обновлён в этой вкладке');
    } else if (payload.webApiToken === '') {
      state.token = '';
      sessionStorage.removeItem(TOKEN_KEY);
    }

    setFormStatus(parts.join(' · '), result.warnings && result.warnings.length ? 'warn' : 'ok');
    await loadConfig();
    refresh();
  } catch (error) {
    if (error instanceof AuthRequired) {
      setFormStatus('Нужен Bearer-токен', 'bad');
      return;
    }

    const problem = error.problem;

    // 409: ключи «только старт» — файл не изменён, менять их можно лишь при запуске (F-2.12).
    if (error.status === 409 && problem && Array.isArray(problem.startupOnly) && problem.startupOnly.length) {
      setFormStatus(
        `Эти ключи применяются только при запуске процесса: ${problem.startupOnly.join(', ')}. ` +
        'Измените их в config.json и перезапустите приложение.',
        'bad',
      );
      return;
    }

    // 409: привязку не удалось открыть — файл и память откачены (F-7.5).
    if (error.status === 409 && problem && problem.rolledBack) {
      setFormStatus(`Не удалось перезапустить слушатель, изменения откатаны: ${error.message}`, 'bad');
      await loadConfig();
      return;
    }

    // 409: перезапуск уже идёт — просим повторить (F-7.6).
    if (error.status === 409) {
      setFormStatus(`${error.message}. Повторите попытку через секунду.`, 'warn');
      return;
    }

    setFormStatus(`Ошибка сохранения: ${error.message}`, 'bad');
  } finally {
    button.disabled = false;
  }
}

async function resetConfig() {
  try {
    const defaults = await apiJson('/api/config/reset', { method: 'POST' });
    fillForm(defaults);
    setFormStatus('Загружены значения по умолчанию — нажмите «Сохранить», чтобы записать их', 'warn');
    state.defaultsLoaded = true;
  } catch (error) {
    if (error instanceof AuthRequired) return;
    setFormStatus(`Не удалось получить значения по умолчанию: ${error.message}`, 'bad');
  }
}

/* ============================ токены доступа ============================ */

// Токен — это секрет прокси (F-8.1). Сервер отдаёт его в открытом виде только в ответ
// на создание, ротацию и запрос /link (F-8.8), поэтому такой секрет живёт лишь в блоке
// #token-reveal и в поле редактирования, но никогда не попадает в состояние панели
// и не записывается в config.json через форму.

function tokenStatus(message, tone) {
  const node = $('token-status');
  node.textContent = message || '';
  node.dataset.tone = tone || '';
}

function jsonRequest(method, payload) {
  return {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  };
}

function tokenLink(secret) {
  const mt = (state.config && state.config.mtproto) || {};
  const publicHost = (mt.publicHost || '').trim();
  if (!publicHost) return null;
  let host = publicHost;
  let port = mt.port || 1443;
  // Внешний адрес может быть host, host:port или [IPv6]:port.
  if (publicHost.startsWith('[')) {
    const m = publicHost.match(/^\[([^\]]+)\](?::(\d+))?$/);
    if (!m) return null;
    host = m[1];
    if (m[2]) port = Number(m[2]);
  } else if (publicHost.includes(':')) {
    const i = publicHost.indexOf(':');
    if (i === publicHost.lastIndexOf(':')) {
      const maybePort = publicHost.slice(i + 1);
      if (/^\d+$/.test(maybePort)) {
        host = publicHost.slice(0, i);
        port = Number(maybePort);
      }
    }
  }
  return `tg://proxy?server=${host}&port=${port}&secret=dd${secret}`;
}

function showTokenReveal(secret, name) {
  $('token-reveal-value').value = secret;
  $('token-reveal-title').textContent = name
    ? `Секрет токена «${name}» показывается один раз`
    : 'Секрет показывается один раз';

  const link = tokenLink(secret);
  $('token-reveal-link').textContent = link
    ? `Ссылка для клиента: ${link}`
    : 'Скопируйте секрет сейчас: ссылка tg:// строится на внешнем адресе прокси, '
      + 'а при запуске прокси печатает готовые ссылки в журнал.';

  $('token-reveal').hidden = false;
  $('token-reveal-value').select();
}

function hideTokenReveal() {
  $('token-reveal').hidden = true;
  $('token-reveal-value').value = '';
  $('token-reveal-link').textContent = '';
}

function actionButton(label, handler, extraClass = '') {
  const button = el('button', `btn btn--small ${extraClass}`.trim(), label);
  button.type = 'button';
  button.addEventListener('click', handler);
  return button;
}

// Копирование в буфер обмена. Clipboard API доступен только в защищённом контексте
// (https/локальный адрес), поэтому при его недоступности копируем через запасной путь
// document.execCommand('copy') с временной textarea — работает и на обычном http.
async function copyText(text) {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // переходим к запасному пути ниже
  }
  return copyTextFallback(text);
}

function copyTextFallback(text) {
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', '');
  textarea.style.position = 'fixed';
  textarea.style.top = '-9999px';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);

  const selection = document.getSelection();
  const savedRange = selection && selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
  textarea.select();
  textarea.setSelectionRange(0, textarea.value.length);

  let copied = false;
  try {
    copied = document.execCommand('copy');
  } catch {
    copied = false;
  }

  document.body.removeChild(textarea);
  if (savedRange && selection) {
    selection.removeAllRanges();
    selection.addRange(savedRange);
  }
  return copied;
}

// Кнопка «Ссылка MTProto»: берёт у сервера полный секрет и копирует готовую ссылку для
// Telegram (F-8.8, исключение для /api/tokens/{id}/link). Ссылка не кэшируется и
// не попадает в состояние панели — только в буфер обмена.
async function copyTokenLink(token) {
  try {
    const result = await apiJson(`/api/tokens/${encodeURIComponent(token.id)}/link`);
    if (!result.link) {
      tokenStatus(
        'Ссылка не строится: внешний адрес прокси не задан. Укажите его в поле '
          + '«Внешний адрес прокси» (блок «Ссылки Telegram») и сохраните конфигурацию.',
        'warn');
      return;
    }

    if (await copyText(result.link)) {
      tokenStatus(`Ссылка для «${token.name}» скопирована в буфер обмена`, 'ok');
    } else {
      tokenStatus(`Ссылка для «${token.name}»: ${result.link}`, 'warn');
    }
  } catch (error) {
    tokenStatus(describeTokenError(error), 'bad');
  }
}

// Кнопка «Ссылка Web»: копирует ссылку-приглашение WEB proxy по каноническому
// шаблону Telegram — https://t.me/webproxy?server=<host>[/<basePath>]&secret=<secret>.
// Секрет в ссылке: обычный 32-hex для корня и base64url(0x70‖secret) при заданном
// basePath. Bridge-URL (адрес страницы-носителя с HMAC capability) в буфер не попадает:
// это отладочный адрес, а не ссылка для Telegram.
async function copyWebProxyLink(token) {
  try {
    const result = await apiJson(`/api/tokens/${encodeURIComponent(token.id)}/link`);
    if (!result.webProxyLink) {
      tokenStatus(
        'WEB proxy ссылка не строится: включите webProxy.enabled и задайте внешний домен '
          + '(webProxy.publicHost). Тип WEB proxy работает только с доменом — IP-адрес '
          + 'Telegram не принимает.',
        'warn');
      return;
    }

    if (await copyText(result.webProxyLink)) {
      const deep = result.webProxyDeepLink ? ` (deep-link: ${result.webProxyDeepLink})` : '';
      tokenStatus(`WEB proxy ссылка для «${token.name}» скопирована${deep}`, 'ok');
    } else {
      tokenStatus(`WEB proxy ссылка для «${token.name}»: ${result.webProxyLink}`, 'warn');
    }
  } catch (error) {
    tokenStatus(describeTokenError(error), 'bad');
  }
}

function renderTokens() {
  const body = $('token-rows');
  const tokens = state.tokens || [];

  body.textContent = '';
  $('token-empty').hidden = tokens.length > 0;
  $('tokens-summary').textContent = tokens.length
    ? `Всего ${tokens.length} · включено ${tokens.filter((token) => token.enabled).length}`
    : '';

  for (const token of tokens) {
    const usage = state.tokenUsage.get(token.id) || {};
    const row = el('tr');

    const nameCell = el('td');
    nameCell.append(el('span', 'table__ip', token.name));
    nameCell.append(el('span', 'table__token mono', token.id));
    row.append(nameCell);

    // GET /api/tokens всегда отдаёт маску секрета (F-8.8, N-8).
    row.append(el('td', 'mono muted', token.secret || '—'));

    const stateCell = el('td');
    const pill = el('span', 'pill', token.enabled ? 'включён' : 'отключён');
    pill.dataset.state = token.enabled ? 'ok' : 'off';
    stateCell.append(pill);
    row.append(stateCell);

    row.append(el('td', 'num', fmtBytes(usage.bytesUp || 0)));
    row.append(el('td', 'num', fmtBytes(usage.bytesDown || 0)));
    row.append(el('td', 'num', `${fmtNum(usage.activeConnections || 0)} / ${fmtNum(usage.totalConnections || 0)}`));
    row.append(el('td', 'muted', token.lastUsedAt ? fmtTime(token.lastUsedAt) : '—'));

    const actions = el('td');
    const group = el('span', 'table__actions');
    group.append(actionButton('Ссылка MTProto', () => copyTokenLink(token)));
    group.append(actionButton('Ссылка Web', () => copyWebProxyLink(token)));
    group.append(actionButton('Изменить', () => startTokenEdit(token)));
    group.append(actionButton('Ротация', () => rotateToken(token)));
    group.append(actionButton(token.enabled ? 'Отключить' : 'Включить', () => toggleToken(token)));
    group.append(actionButton('Удалить', () => deleteToken(token), 'btn--danger'));
    actions.append(group);
    row.append(actions);

    body.append(row);
  }
}

function resetTokenForm() {
  state.editingTokenId = null;
  $('token-form').reset();
  $('token-enabled').checked = true;
  $('token-secret').placeholder = 'пусто — сгенерируется';
  $('token-submit').textContent = 'Создать токен';
  $('token-cancel').hidden = true;
  tokenStatus('');
}

function startTokenEdit(token) {
  state.editingTokenId = token.id;
  $('token-name').value = token.name || '';
  // Поле сначала пустое: сервер отдаёт полный секрет только по запросу /link.
  // Пустое поле означает «не менять» (F-8.8), полное — подставляется ниже.
  $('token-secret').value = '';
  $('token-secret').placeholder = 'пусто — не менять';
  $('token-enabled').checked = Boolean(token.enabled);
  $('token-submit').textContent = 'Сохранить токен';
  $('token-cancel').hidden = false;
  tokenStatus(`Изменение токена «${token.name}»`);
  $('token-name').focus();

  // Подставляем текущий секрет, чтобы при редактировании поле отражало реальное
  // значение токена (запрос F-8.8 через /api/tokens/{id}/link).
  apiJson(`/api/tokens/${encodeURIComponent(token.id)}/link`)
    .then((result) => {
      // Пользователь мог уйти к другому токену или отменить правку.
      if (state.editingTokenId !== token.id || !result.secret) return;
      $('token-secret').value = result.secret;
      $('token-secret').placeholder = 'текущий секрет — измените, чтобы ротировать';
    })
    .catch(() => {
      // Без полного секрета поле остаётся пустым и означает «не менять».
    });
}

async function submitToken(event) {
  event.preventDefault();

  const name = $('token-name').value.trim();
  const secret = $('token-secret').value.trim();
  const enabled = $('token-enabled').checked;

  if (!name) {
    tokenStatus('Укажите имя токена', 'bad');
    $('token-name').focus();
    return;
  }

  if (secret && !SECRET.test(secret)) {
    tokenStatus('Секрет — ровно 32 hex-символа', 'bad');
    $('token-secret').focus();
    return;
  }

  const payload = { name, enabled };
  if (secret) payload.secret = secret;

  const editing = state.editingTokenId;
  const submit = $('token-submit');
  submit.disabled = true;
  tokenStatus(editing ? 'Сохранение…' : 'Создание…');

  try {
    if (editing) {
      const result = await apiJson(`/api/tokens/${encodeURIComponent(editing)}`, jsonRequest('PUT', payload));
      hideTokenReveal();
      resetTokenForm();
      tokenStatus(
        result.closedSessions
          ? `Токен «${result.token.name}» сохранён, разорвано сессий: ${result.closedSessions}`
          : `Токен «${result.token.name}» сохранён`,
        'ok');
    } else {
      const created = await apiJson('/api/tokens', jsonRequest('POST', payload));
      resetTokenForm();
      showTokenReveal(created.secret, created.name);
      tokenStatus(`Токен «${created.name}» создан`, 'ok');
    }

    await refresh();
  } catch (error) {
    tokenStatus(describeTokenError(error), 'bad');
  } finally {
    submit.disabled = false;
  }
}

async function toggleToken(token) {
  tokenStatus(token.enabled ? 'Отключение…' : 'Включение…');

  try {
    const result = await apiJson(
      `/api/tokens/${encodeURIComponent(token.id)}`,
      jsonRequest('PUT', { enabled: !token.enabled }));

    tokenStatus(
      token.enabled
        ? `Токен «${result.token.name}» отключён, разорвано сессий: ${result.closedSessions}`
        : `Токен «${result.token.name}» включён`,
      'ok');
    await refresh();
  } catch (error) {
    tokenStatus(describeTokenError(error), 'bad');
  }
}

async function rotateToken(token) {
  const confirmed = window.confirm(
    `Сгенерировать новый секрет для токена «${token.name}»?\n\n`
    + 'Старый секрет перестанет работать: клиенты с ним не смогут подключиться. '
    + 'Уже установленные сессии сохранятся.');
  if (!confirmed) return;

  tokenStatus('Ротация…');

  try {
    const rotated = await apiJson(`/api/tokens/${encodeURIComponent(token.id)}/rotate`, jsonRequest('POST', {}));
    showTokenReveal(rotated.secret, rotated.name);
    tokenStatus(`Секрет токена «${rotated.name}» обновлён`, 'ok');
    await refresh();
  } catch (error) {
    tokenStatus(describeTokenError(error), 'bad');
  }
}

async function deleteToken(token) {
  const confirmed = window.confirm(
    `Удалить токен «${token.name}»?\n\n`
    + 'Его сессии будут разорваны, а клиенты с этим секретом потеряют доступ.');
  if (!confirmed) return;

  tokenStatus('Удаление…');

  try {
    const result = await apiJson(`/api/tokens/${encodeURIComponent(token.id)}`, { method: 'DELETE' });
    hideTokenReveal();
    resetTokenForm();
    tokenStatus(
      result.closedSessions
        ? `Токен удалён, разорвано сессий: ${result.closedSessions}`
        : 'Токен удалён',
      'ok');
    await refresh();
  } catch (error) {
    tokenStatus(describeTokenError(error), 'bad');
  }
}

// Ошибки токенов приходят как ProblemDetails: 400 — нарушение правил (F-8.2),
// 409 — дубликат имени или лимит количества токенов (F-8.8).
function describeTokenError(error) {
  if (error instanceof AuthRequired) return 'Нужен Bearer-токен Web API';
  const problem = error.problem;
  if (problem && problem.status === 409 && problem.name) {
    return `Имя «${problem.name}» уже занято другим токеном`;
  }
  return error.message || 'Не удалось выполнить операцию';
}

/* ============================ доступность DC ============================ */

// Порядок колонок совпадает с маршрутами сервера (IDcReachabilityChecker):
// прямой WebSocket, прямой TCP, Cloudflare-фронтинг, Cloudflare Worker.
const DC_ROUTES = [
  { key: 'directWs', label: 'Прямой WebSocket' },
  { key: 'directTcp', label: 'Прямой TCP' },
  { key: 'cfProxy', label: 'Cloudflare-фронтинг' },
  { key: 'cfWorker', label: 'Cloudflare Worker' },
];

async function runDcCheck() {
  const button = $('dc-check');
  const status = $('dc-status');
  button.disabled = true;
  button.textContent = 'Проверяем…';
  status.textContent = '';
  status.dataset.tone = '';
  try {
    const report = await apiJson('/api/dc-check', { method: 'POST' });
    state.dcReport = report;
    renderDcCheck(report);
    updateDcFillButton();
  } catch (error) {
    if (error instanceof AuthRequired) {
      if (!tokenPromptShown) {
        tokenPromptShown = true;
        showBanner('Web API защищён токеном: введите Bearer-токен, чтобы видеть данные.', 'auth');
      }
    } else {
      status.textContent = `Не удалось выполнить проверку: ${error.message}`;
      status.dataset.tone = 'bad';
    }
  } finally {
    button.disabled = false;
    button.textContent = 'Проверить';
  }
}

function dcCell(result) {
  const cell = el('td');
  if (!result) {
    cell.append(el('span', 'muted', '—'));
    return cell;
  }
  cell.append(el(
    'span',
    `pill dc-pill ${result.ok ? 'dc-pill--ok' : 'dc-pill--fail'}`,
    result.ok ? 'доступен' : 'недоступен',
  ));
  if (result.ok && result.latencyMs != null) {
    cell.append(el('span', 'muted dc-ms', ` ${fmtNum(result.latencyMs)} мс`));
  }
  if (result.ok && result.ip) {
    cell.append(el('div', 'muted dc-detail', `IP ${result.ip}`));
  } else if (result.ok && result.detail) {
    cell.append(el('div', 'muted dc-detail', result.detail));
  } else if (!result.ok) {
    const lines = [result.error, result.detail].filter(Boolean);
    cell.append(el('div', 'muted dc-detail', lines.join(' · ')));
  }
  return cell;
}

function renderDcCheck(report) {
  const head = $('dc-head');
  head.textContent = '';
  const headRow = el('tr');
  const dcCol = el('th', '', 'DC');
  dcCol.scope = 'col';
  headRow.append(dcCol);
  for (const route of DC_ROUTES) {
    const found = report.routes.find((r) => r.route === route.key);
    const th = el('th', '', found && found.label ? found.label : route.label);
    th.scope = 'col';
    headRow.append(th);
  }
  head.append(headRow);

  const body = $('dc-rows');
  body.textContent = '';
  const byRoute = new Map(report.routes.map((r) => [r.route, r]));
  for (const dc of report.dcs) {
    const row = el('tr');
    const dcHeader = el('th', 'dc-num', `DC ${dc}`);
    dcHeader.scope = 'row';
    row.append(dcHeader);
    for (const route of DC_ROUTES) {
      const r = byRoute.get(route.key);
      const result = r && r.results.find((x) => x.dc === dc);
      row.append(dcCell(result));
    }
    body.append(row);
  }

  const notes = report.routes
    .filter((r) => r.note)
    .map((r) => `${r.label || r.route}: ${r.note}`)
    .join(' · ');
  $('dc-summary').textContent = `Проверено ${report.dcs.length} DC · ${fmtTime(report.checkedAt)}${notes ? ` · ${notes}` : ''}`;
}

// Собирает рабочие IP маршрута «Прямой TCP» из последнего отчёта проверки DC.
function collectDcFill() {
  const report = state.dcReport;
  if (!report) return [];
  const route = report.routes.find((r) => r.route === 'directTcp');
  if (!route) return [];
  return route.results
    .filter((r) => r.ok && r.ip)
    .sort((a, b) => a.dc - b.dc)
    .map((r) => `${r.dc}:${r.ip}`);
}

function updateDcFillButton() {
  const button = $('dc-fill');
  if (!button) return;
  const entries = collectDcFill();
  button.disabled = entries.length === 0;
  button.title = entries.length === 0
    ? 'Сначала выполните проверку: кнопка активна, когда есть DC, доступные по прямому TCP'
    : `Подставить в «DC → IP»: ${entries.join(', ')}`;
}

function fillDcIpFromCheck() {
  const entries = collectDcFill();
  if (entries.length === 0) return;

  const input = $(fieldId('dcIp'));
  if (!input) return;

  input.value = entries.join('\n');
  refreshCategoryBadges();

  const wrapper = document.querySelector('.field[data-key="dcIp"]');
  if (wrapper) wrapper.scrollIntoView({ behavior: 'smooth', block: 'center' });

  showBanner(
    `Подставлены IP для ${entries.length} DC в поле «DC → IP». Проверьте список и нажмите «Сохранить» — применится с перезапуском слушателя.`,
    'warn',
  );
}

/* ============================ инициализация ============================ */

function initFilter() {
  const input = $('peer-filter');
  let handle = null;

  input.addEventListener('input', () => {
    state.peerFilter = input.value.trim();
    if (handle !== null) clearTimeout(handle);
    // Фильтр уходит на сервер: он же отвечает за сортировку и пагинацию.
    handle = setTimeout(() => {
      handle = null;
      refresh();
    }, 220);
  });

  // Группировка источников: пары (IP, токен), только IP или только токены (F-9.4).
  const group = $('peer-group');
  group.addEventListener('change', () => {
    state.peerGroup = group.value;
    refresh();
  });
}

function initTokens() {
  $('token-form').addEventListener('submit', submitToken);
  $('token-cancel').addEventListener('click', resetTokenForm);

  $('token-reveal-copy').addEventListener('click', async () => {
    const value = $('token-reveal-value');
    try {
      await navigator.clipboard.writeText(value.value);
      tokenStatus('Секрет скопирован в буфер обмена', 'ok');
    } catch {
      // Буфер обмена недоступен (нет https или запрет браузера) — выделяем текст.
      value.select();
      tokenStatus('Скопируйте секрет вручную: Ctrl+C', 'warn');
    }
  });

  $('token-reveal-close').addEventListener('click', hideTokenReveal);
  resetTokenForm();
}

function init() {
  initTheme();
  buildForm();
  initFilter();
  initTokens();

  $('config-form').addEventListener('submit', saveConfig);
  // Пометка «перезапуск слушателя» появляется/исчезает по мере правки полей.
  $('config-form').addEventListener('input', refreshCategoryBadges);
  $('config-form').addEventListener('change', refreshCategoryBadges);
  $('config-reload').addEventListener('click', async () => {
    setFormStatus('Перезагрузка…', '');
    const config = await loadConfig();
    setFormStatus(config ? 'Конфигурация перечитана с сервера' : '', config ? 'ok' : 'bad');
  });
  $('config-reset').addEventListener('click', resetConfig);
  $('dc-check').addEventListener('click', runDcCheck);
  $('dc-fill').addEventListener('click', fillDcIpFromCheck);

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      refresh();
    }
  });

  // Пауза опроса при уходе со страницы — не держим интервал зря.
  window.addEventListener('pagehide', stopPolling);
  window.addEventListener('pageshow', () => {
    refresh();
    startPolling();
  });

  loadConfig();
  refresh();
  startPolling();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init);
} else {
  init();
}
