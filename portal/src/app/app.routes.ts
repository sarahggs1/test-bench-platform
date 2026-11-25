import { Routes } from '@angular/router';
import { ConfigurationsPage } from './configurations-page';
import { Dashboard } from './dashboard';
import { RunPage } from './run-page';
import { TestsPage } from './tests-page';

export const routes: Routes = [
  { path: '', component: Dashboard, title: 'Bench – Test Bench Portal' },
  { path: 'tests', component: TestsPage, title: 'Tests – Test Bench Portal' },
  { path: 'runs/:id', component: RunPage, title: 'Run – Test Bench Portal' },
  { path: 'configurations', component: ConfigurationsPage, title: 'Configurations – Test Bench Portal' },
  { path: '**', redirectTo: '' },
];
