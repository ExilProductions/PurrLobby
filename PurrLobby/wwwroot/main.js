async function fetchJson(url) {
  const r = await fetch(url, { credentials: 'include' });
  if (!r.ok) throw new Error(`Request failed: ${r.status}`);
  return r.json();
}

async function loadGlobal() {
  try {
    const [players, lobbies] = await Promise.all([
      fetchJson('/stats/global/players'),
      fetchJson('/stats/global/lobbies')
    ]);
    document.getElementById('globalPlayers').textContent = players;
    document.getElementById('globalLobbies').textContent = lobbies;
  } catch (e) {
    console.error(e);
  }
}

async function loadGame(gameId) {
  try {
    const [lobbies, players] = await Promise.all([
      fetchJson(`/stats/${gameId}/lobbies`),
      fetchJson(`/stats/${gameId}/players`)
    ]);
    document.getElementById('gameLobbies').textContent = lobbies;
    document.getElementById('gamePlayers').textContent = players.length ?? players; // endpoint returns list
  } catch (e) {
    console.error(e);
  }
}

async function setGameCookie(gameId) {
  const r = await fetch('/session/game', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ gameId }),
    credentials: 'include'
  });
  if (!r.ok) throw new Error('Failed to set game cookie');
  return r.json();
}

function getCookie(name) {
  const m = document.cookie.match(new RegExp('(^| )' + name + '=([^;]+)'));
  return m ? decodeURIComponent(m[2]) : null;
}

(async () => {
  await loadGlobal();

  const gameForm = document.getElementById('gameForm');
  const gameIdInput = document.getElementById('gameIdInput');
  const cookieStatus = document.getElementById('cookieStatus');

  //if cookie exists prefill and load stats
  const existing = getCookie('gameId');
  if (existing) {
    gameIdInput.value = existing;
    loadGame(existing);
    cookieStatus.textContent = `Using gameId from cookie.`;
  }

  gameForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    const gameId = gameIdInput.value.trim();
    if (!/^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\}?$/.test(gameId)) {
      alert('Please enter a valid GUID');
      return;
    }
    try {
      await setGameCookie(gameId);
      cookieStatus.textContent = 'GameId stored in cookie.';
      await loadGame(gameId);
    } catch (err) {
      cookieStatus.textContent = 'Failed to set cookie';
      console.error(err);
    }
  });
})();
