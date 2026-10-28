import { Routes } from '@angular/router';
import { PaymentsComponent } from './pages/payments.component';

export const routes: Routes = [
  { path: 'payments', component: PaymentsComponent },
  { path: '', redirectTo: 'payments', pathMatch: 'full' }
];
