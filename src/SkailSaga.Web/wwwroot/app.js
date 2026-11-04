const STATES = {
  Received: 'Received', NotAccepted: 'Not accepted', Running: 'Running', Confirmed: 'Confirmed',
  OutOfStock: 'Out of stock', Declined: 'Payment declined', Refunded: 'Cancelled with refund',
};
const KINDS = {
  StockReserved: 'Stock reserved', OutOfStock: 'Out of stock', ChargeTimeout: 'Gateway timeout',
  ChargeApproved: 'Card charged', ChargeDeclined: 'Card declined', InvoiceAuthorized: 'Invoice issued',
  InvoiceRejected: 'Invoice rejected', ChargeRefunded: 'Charge refunded', StockReleased: 'Stock released',
  CustomerNotified: 'Customer notified',
};

const $ = (id) => document.getElementById(id);
const els = { scenarios: $('scenarios'), reset: $('reset'), sent: $('sent'), error: $('error'), stock: $('stock'), orders: $('orders'), ordersEmpty: $('ordersEmpty') };

function showError(message) {
  els.error.hidden = !message;
  els.error.textContent = message ?? '';
}

// text only: never build HTML from data
function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function money(value) {
  return 'R$ ' + Number(value).toFixed(2);
}

function renderOrder(order) {
  const card = el('article', 'order');

  const head = el('div', 'order-head');
  head.append(el('span', 'id', order.orderId));
  head.append(el('span', 'meta', `${order.quantity} × ${order.product} · ${money(order.amount)} · ${order.card}`));
  const execution = el('span', 'meta mono', order.taskId.slice(0, 8));
  execution.title = order.taskId;
  head.append(execution);
  head.append(el('span', `badge ${order.state}`, STATES[order.state] ?? order.state));
  card.append(head);

  if (order.steps.length > 0) {
    const list = el('ol', 'steps');
    for (const step of order.steps) {
      const item = el('li', [step.compensation ? 'compensation' : '', step.kind === 'ChargeTimeout' ? 'timeout' : ''].join(' ').trim());
      item.append(el('span', 'kind', KINDS[step.kind] ?? step.kind));
      item.append(el('span', 'detail', step.detail));
      list.append(item);
    }
    card.append(list);
  }

  if (order.result) card.append(el('p', 'result', `Result: ${order.result}`));
  return card;
}

function render(state) {
  els.stock.replaceChildren();
  for (const s of state.stock) {
    const row = els.stock.insertRow();
    row.insertCell().textContent = s.product;
    row.insertCell().textContent = String(s.available);
    row.insertCell().textContent = String(s.reserved);
  }

  els.orders.replaceChildren(...[...state.orders].reverse().map(renderOrder));
  els.ordersEmpty.hidden = state.orders.length > 0;
}

// the scenario buttons call the real endpoint, like any client of the order API would
async function placeOrder(order) {
  showError(null);
  els.sent.textContent = 'placing the order…';
  try {
    const response = await fetch('/api/orders', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(order),
    });
    const body = await response.json().catch(() => ({}));
    els.sent.textContent = `${body.orderId ?? 'order'} → ${response.status}` + (body.taskId ? ` · execution ${body.taskId.slice(0, 8)}` : '');
    if (!response.ok) showError(body.error ?? `the order API answered ${response.status}`);
  } catch (e) {
    els.sent.textContent = '';
    showError(`could not reach the order API: ${e.message}`);
  }
}

els.scenarios.addEventListener('click', (event) => {
  const button = event.target.closest('button[data-product]');
  if (!button) return;
  placeOrder({
    product: button.dataset.product,
    quantity: Number(button.dataset.quantity),
    amount: Number(button.dataset.amount),
    card: button.dataset.card,
  });
});

els.reset.addEventListener('click', async () => {
  showError(null);
  await fetch('/api/reset', { method: 'POST' });
  els.sent.textContent = '';
});

// the snapshot comes first and again on every change; EventSource reconnects by itself
const source = new EventSource('/api/stream');
source.onmessage = (message) => render(JSON.parse(message.data));
source.onerror = () => { /* the browser retries; the last snapshot stays on screen */ };
