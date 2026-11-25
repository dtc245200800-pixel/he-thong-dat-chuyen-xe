const API_URL = window.RIDE_API_URL || 'http://localhost:8080';
const NOTIFY_URL = window.RIDE_NOTIFY_URL || 'http://localhost:8081';
const form = document.querySelector('#booking-form');
const tripsBox = document.querySelector('#trip-list');
const msg = document.querySelector('#form-message');
let toastTimer;

const statusNames = { Requested: 'Đang tìm tài xế', Matched: 'Đã tìm thấy tài xế', InProgress: 'Đang thực hiện', Completed: 'Hoàn thành', Cancelled: 'Đã hủy' };
const fmtTime = value => new Intl.DateTimeFormat('vi-VN', { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' }).format(new Date(value));
function notify(text) { const t = document.querySelector('#toast'); t.textContent = text; t.classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 2800); }
async function request(path, options = {}) { const response = await fetch(`${API_URL}${path}`, { ...options, headers: { 'Content-Type': 'application/json', ...(options.headers || {}) } }); if (!response.ok) { let detail = `Yêu cầu lỗi (${response.status})`; try { const data = await response.json(); detail = data.error || Object.values(data.errors || {}).flat().join(' ') || detail; } catch {} throw new Error(detail); } return response.status === 204 ? null : response.json(); }

async function loadDrivers() {
  const drivers = await request('/drivers/available');
  document.querySelector('#driver-count').textContent = `${drivers.length} rảnh`;
  document.querySelector('#drivers-list').innerHTML = drivers.length ? drivers.map(d => `<div class="driver-chip"><span class="driver-avatar">${escapeHtml(d.name.split(' ').map(x => x[0]).slice(-2).join(''))}</span><span><strong>${escapeHtml(d.name)}</strong><small>Đang sẵn sàng</small></span></div>`).join('') : '<span class="trip-meta">Hiện chưa có tài xế rảnh.</span>';
}
async function loadTrips() {
  const passenger = encodeURIComponent(document.querySelector('#passenger').value.trim());
  if (!passenger) { tripsBox.innerHTML = emptyTrips(); return; }
  const trips = await request(`/users/${passenger}/trips`);
  if (!trips.length) { tripsBox.innerHTML = emptyTrips(); return; }
  tripsBox.innerHTML = trips.map(t => `<article class="trip-item"><div class="trip-top"><span class="trip-route">${escapeHtml(t.pickup)} <span style="color:#91a097">→</span> ${escapeHtml(t.destination)}</span><span class="status-pill status-${t.status}">${statusNames[t.status] || t.status}</span></div><div class="trip-meta">${fmtTime(t.createdAt)} · Mã ${t.id.slice(0, 8)}</div>${t.driver ? `<div class="trip-driver">✳ Tài xế: ${escapeHtml(t.driver.name)}</div>` : ''}${actions(t)}</article>`).join('');
  tripsBox.querySelectorAll('[data-status]').forEach(button => button.addEventListener('click', () => changeStatus(button.dataset.trip, button.dataset.status)));
}
function emptyTrips() { return '<div class="empty-state"><div class="empty-icon">↗</div><strong>Chưa có chuyến xe</strong><span>Chuyến bạn đặt sẽ xuất hiện ở đây.</span></div>'; }
function actions(t) { if (t.status === 'Matched') return `<div class="trip-actions"><button data-trip="${t.id}" data-status="InProgress">Bắt đầu chuyến</button><button data-trip="${t.id}" data-status="Cancelled">Hủy chuyến</button></div>`; if (t.status === 'InProgress') return `<div class="trip-actions"><button data-trip="${t.id}" data-status="Completed">Hoàn thành chuyến</button><button data-trip="${t.id}" data-status="Cancelled">Hủy chuyến</button></div>`; if (t.status === 'Requested') return `<div class="trip-actions"><button data-trip="${t.id}" data-status="Cancelled">Hủy yêu cầu</button></div>`; return ''; }
async function changeStatus(id, status) { try { await request(`/trips/${id}/status`, { method: 'PATCH', body: JSON.stringify({ status }) }); notify(`Đã cập nhật: ${statusNames[status]}`); await Promise.all([loadTrips(), loadDrivers()]); } catch (e) { notify(e.message); } }
function escapeHtml(value) { return String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

form.addEventListener('submit', async event => {
  event.preventDefault(); msg.textContent = ''; const button = document.querySelector('#submit-button'); button.disabled = true;
  const data = Object.fromEntries(new FormData(form));
  for (const key of ['pickupLatitude', 'pickupLongitude', 'destinationLatitude', 'destinationLongitude']) data[key] = Number(data[key]);
  try { const trip = await request('/trips', { method: 'POST', body: JSON.stringify(data) }); notify('Đã gửi yêu cầu. Đang tìm tài xế cho bạn!'); await loadTrips(); if (connection) await connection.invoke('JoinTrip', trip.id); }
  catch (e) { msg.textContent = e.message; } finally { button.disabled = false; }
});
document.querySelector('#refresh-button').addEventListener('click', () => refresh());
async function refresh() { try { await Promise.all([loadTrips(), loadDrivers()]); } catch (e) { document.querySelector('#connection-label').textContent = 'Không kết nối được máy chủ'; document.querySelector('#connection-dot').classList.add('muted'); } }
let connection;
async function connectLive() {
  if (!window.signalR) { document.querySelector('#connection-label').textContent = 'Cập nhật trực tiếp chưa sẵn sàng'; return; }
  connection = new signalR.HubConnectionBuilder().withUrl(`${NOTIFY_URL}/hubs/trips`).withAutomaticReconnect().build();
  connection.on('TripStatusChanged', event => { if (event.passengerId === document.querySelector('#passenger').value.trim()) { notify(event.message || `Chuyến xe: ${statusNames[event.status] || event.status}`); refresh(); } });
  connection.onreconnecting(() => { document.querySelector('#connection-label').textContent = 'Đang kết nối lại…'; document.querySelector('#connection-dot').classList.add('muted'); });
  connection.onreconnected(async () => { document.querySelector('#connection-label').textContent = 'Đang nhận cập nhật trực tiếp'; document.querySelector('#connection-dot').classList.remove('muted'); await connection.invoke('JoinPassenger', document.querySelector('#passenger').value.trim()); });
  try { await connection.start(); await connection.invoke('JoinPassenger', document.querySelector('#passenger').value.trim()); document.querySelector('#connection-label').textContent = 'Đang nhận cập nhật trực tiếp'; document.querySelector('#connection-dot').classList.remove('muted'); }
  catch { document.querySelector('#connection-label').textContent = 'Chưa kết nối cập nhật trực tiếp'; }
}
refresh(); connectLive();
