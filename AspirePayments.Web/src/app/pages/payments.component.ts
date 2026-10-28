import { Component, DestroyRef, PLATFORM_ID, inject, OnInit, signal } from '@angular/core';
import { DatePipe, DecimalPipe, isPlatformBrowser } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import {
  Order,
  PaymentApiService,
  PaymentScenario
} from '../services/payment-api.service';
import { PaymentEventsService } from '../services/payment-events.service';
import { ToastService } from '../services/toasted.service';

/** Item da lista de transições exibida no feed em tempo real. */
interface PaymentEventFeedItem {
  orderId: string;
  orderStatus: string;
  paymentStatus: string;
  amount: number;
  currency: string;
  failureReason: string | null;
  occurredAt: string;
}

@Component({
  selector: 'app-payments',
  imports: [DatePipe, DecimalPipe],
  template: `
    <h2>Processamento de pagamentos</h2>

    <section class="card">
      <h3>Novo pedido</h3>

      <div class="form-grid">
        <label>
          Valor
          <input type="number" step="0.01" min="0" [value]="amount()" (input)="onAmount($event)" />
        </label>

        <label>
          Método de pagamento
          <select [value]="paymentMethod()" (change)="onPaymentMethod($event)">
            <option value="SimulatedCard">SimulatedCard</option>
            <option value="SimulatedBoleto">SimulatedBoleto</option>
            <option value="SimulatedPix">SimulatedPix</option>
          </select>
        </label>

        <label>
          Cenário do gateway
          <select [value]="scenario()" (change)="onScenario($event)">
            <option value="Succeed">Succeed (aprovado)</option>
            <option value="Fail">Fail (recusado)</option>
            <option value="FailAfterCommit">FailAfterCommit (falha após o commit)</option>
            <option value="FailTransient">FailTransient (falha transitória)</option>
          </select>
        </label>

        <label>
          Chave de idempotência (opcional)
          <input
            type="text"
            placeholder="deixe vazio para gerar"
            [value]="idempotencyKey()"
            (input)="onIdempotencyKey($event)" />
        </label>
      </div>

      <button type="button" (click)="createOrder()" [disabled]="creating()">
        {{ creating() ? 'Enviando...' : 'Criar pedido e pagar' }}
      </button>

      @if (error()) {
        <p class="error">{{ error() }}</p>
      }
    </section>

    <section class="card">
      <h3>Pedidos</h3>

      <button type="button" (click)="loadOrders()">Atualizar</button>

      @if (orders().length === 0) {
        <p>Nenhum pedido criado ainda.</p>
      }

      <table>
        <thead>
          <tr>
            <th>OrderId</th>
            <th>Valor</th>
            <th>Status do pedido</th>
            <th>Pagamento</th>
            <th>Cenário</th>
            <th>Motivo da falha</th>
            <th>Criado em</th>
          </tr>
        </thead>
        <tbody>
          @for (order of orders(); track order.orderId) {
            <tr>
              <td class="mono">{{ shortId(order.orderId) }}</td>
              <td>{{ order.totalAmount | number: '1.2-2' }} {{ order.currency }}</td>
              <td>
                <span class="badge" [class]="'badge badge-' + order.status.toLowerCase()">
                  {{ order.status }}
                </span>
              </td>
              <td>
                @for (payment of order.payments; track payment.paymentTransactionId) {
                  <div>
                    <span class="badge" [class]="'badge badge-' + payment.status.toLowerCase()">
                      {{ payment.status }}
                    </span>
                    <span class="mono">{{ shortId(payment.paymentTransactionId) }}</span>
                  </div>
                }
              </td>
              <td>{{ order.payments[0]?.scenario ?? '-' }}</td>
              <td>{{ order.payments[0]?.failureReason ?? '-' }}</td>
              <td>{{ order.createdAt | date: 'HH:mm:ss' }}</td>
            </tr>
          }
        </tbody>
      </table>
    </section>

    <section class="card">
      <h3>Transições em tempo real (SSE)</h3>

      @if (events().length === 0) {
        <p>Aguardando eventos de pagamento...</p>
      }

      <ul class="feed">
        @for (event of events(); track event.occurredAt + event.orderId) {
          <li>
            <span class="badge" [class]="'badge badge-' + event.orderStatus.toLowerCase()">
              {{ event.orderStatus }}
            </span>
            <span class="mono">{{ shortId(event.orderId) }}</span>
            <span>{{ event.paymentStatus }}</span>
            <span>{{ event.amount | number: '1.2-2' }} {{ event.currency }}</span>
            @if (event.failureReason) {
              <span class="error">{{ event.failureReason }}</span>
            }
            <small>{{ event.occurredAt | date: 'HH:mm:ss' }}</small>
          </li>
        }
      </ul>
    </section>
  `,
  styles: `
    .card {
      border: 1px solid #d7dde5;
      border-radius: 8px;
      padding: 16px;
      margin-bottom: 16px;
    }

    .form-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 12px;
      margin-bottom: 12px;
    }

    label {
      display: flex;
      flex-direction: column;
      gap: 4px;
      font-size: 14px;
    }

    input,
    select {
      padding: 8px;
      border: 1px solid #c3cbd5;
      border-radius: 4px;
    }

    button {
      padding: 8px 14px;
      border: 1px solid #2f6feb;
      border-radius: 4px;
      background: #2f6feb;
      color: #fff;
      cursor: pointer;
    }

    button:disabled {
      opacity: 0.6;
      cursor: not-allowed;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      margin-top: 12px;
      font-size: 14px;
    }

    th,
    td {
      text-align: left;
      padding: 6px 8px;
      border-bottom: 1px solid #eceff3;
      vertical-align: top;
    }

    .badge {
      display: inline-block;
      padding: 2px 8px;
      border-radius: 10px;
      background: #eef1f5;
      font-size: 12px;
      font-weight: 600;
    }

    .badge-paid,
    .badge-succeeded {
      background: #dcf5e4;
      color: #166534;
    }

    .badge-failed,
    .badge-cancelled {
      background: #fde2e2;
      color: #991b1b;
    }

    .badge-pendingpayment,
    .badge-pending,
    .badge-processing {
      background: #fdf0d5;
      color: #92400e;
    }

    .badge-refunded,
    .badge-compensated {
      background: #dbeafe;
      color: #1e40af;
    }

    .feed {
      list-style: none;
      padding: 0;
      margin: 0;
      font-size: 14px;
    }

    .feed li {
      display: flex;
      gap: 10px;
      align-items: center;
      padding: 6px 0;
      border-bottom: 1px solid #eceff3;
    }

    .mono {
      font-family: ui-monospace, 'Cascadia Code', monospace;
      font-size: 12px;
      color: #57606a;
    }

    .error {
      color: #b42318;
    }
  `
})
export class PaymentsComponent implements OnInit {
  private readonly api = inject(PaymentApiService);
  private readonly eventsService = inject(PaymentEventsService);
  private readonly toastService = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly platformId = inject(PLATFORM_ID);

  readonly orders = signal<Order[]>([]);
  readonly events = signal<PaymentEventFeedItem[]>([]);

  readonly amount = signal(149.9);
  readonly paymentMethod = signal('SimulatedCard');
  readonly scenario = signal<PaymentScenario>('Succeed');
  readonly idempotencyKey = signal('');

  readonly creating = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    // Os dados vêm da API e do stream SSE, que só existem no browser.
    if (!isPlatformBrowser(this.platformId)) {
      return;
    }

    this.loadOrders();

    this.eventsService
      .connect()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: event => {
          this.events.update(current => [
            {
              orderId: event.orderId,
              orderStatus: event.orderStatus,
              paymentStatus: event.paymentStatus,
              amount: event.amount,
              currency: event.currency,
              failureReason: event.failureReason ?? null,
              occurredAt: event.occurredAt
            },
            ...current
          ].slice(0, 20));

          this.toastService.show(
            `Pedido ${this.shortId(event.orderId)} — ${event.orderStatus} (${event.paymentStatus})`
          );

          this.loadOrders();
        },
        error: error => console.error('Erro no stream de pagamentos:', error)
      });
  }

  loadOrders(): void {
    this.api
      .listOrders()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: orders => this.orders.set(orders),
        error: error => console.error('Erro ao carregar pedidos:', error)
      });
  }

  createOrder(): void {
    this.creating.set(true);
    this.error.set(null);

    const key = this.idempotencyKey().trim();

    this.api
      .createOrder(
        {
          totalAmount: Number(this.amount()),
          currency: 'BRL',
          paymentMethod: this.paymentMethod(),
          scenario: this.scenario()
        },
        key || undefined
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: response => {
          this.creating.set(false);

          this.toastService.show(
            response.isIdempotentReplay
              ? `Chave de idempotência já usada — pedido ${this.shortId(response.orderId)} sem novo pagamento`
              : `Pedido ${this.shortId(response.orderId)} criado; pagamento solicitado`
          );

          this.loadOrders();
        },
        error: error => {
          this.creating.set(false);
          this.error.set(this.extractError(error));
        }
      });
  }

  onAmount(event: Event): void {
    this.amount.set(Number((event.target as HTMLInputElement).value));
  }

  onPaymentMethod(event: Event): void {
    this.paymentMethod.set((event.target as HTMLSelectElement).value);
  }

  onScenario(event: Event): void {
    this.scenario.set((event.target as HTMLSelectElement).value as PaymentScenario);
  }

  onIdempotencyKey(event: Event): void {
    this.idempotencyKey.set((event.target as HTMLInputElement).value);
  }

  shortId(value: string): string {
    return value ? value.substring(0, 8) : '';
  }

  private extractError(error: unknown): string {
    const detail = (error as { error?: { detail?: string; title?: string } })?.error;

    return detail?.detail ?? detail?.title ?? 'Não foi possível criar o pedido.';
  }
}
