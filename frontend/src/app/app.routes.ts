import { Routes } from '@angular/router';
import { DomusflowShell } from './domusflow-shell';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'login', component: DomusflowShell },
  { path: 'dashboard', component: DomusflowShell },
  { path: '**', redirectTo: 'dashboard' },
];
