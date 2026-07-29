import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

interface User {
  id: string;
  name: string;
  email: string;
  role: 'OWNER' | 'EMPLOYEE' | 'CHILD';
  avatar: string;
}
interface Task {
  id: string;
  title: string;
  description: string;
  scheduledDate: string;
  startTime?: string;
  estimatedMinutes: number;
  priority: number;
  status: string;
  xpAwarded: number;
  assignee: User;
}

@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  Math = Math;
  api = '/api';
  user = signal<User | null>(null);
  tasks = signal<Task[]>([]);
  users = signal<User[]>([]);
  dashboard = signal<any>({});
  rewards = signal<any[]>([]);
  busy = signal(false);
  error = signal('');
  notice = signal('');
  selectedDate = new Date().toISOString().slice(0, 10);
  showTaskForm = false;
  loginData = { email: 'ana@demo.local', password: 'Demo123!' };
  taskForm: any = {
    title: '',
    description: '',
    scheduledDate: this.selectedDate,
    startTime: '08:00',
    estimatedMinutes: 30,
    priority: 2,
    assigneeId: '',
  };
  constructor(private http: HttpClient) {}
  ngOnInit() {
    if (localStorage.getItem('domusflow_token')) this.loadAll();
  }
  login() {
    this.busy.set(true);
    this.error.set('');
    this.http.post<any>(`${this.api}/auth/login`, this.loginData).subscribe({
      next: (r) => {
        localStorage.setItem('domusflow_token', r.token);
        this.user.set(r.user);
        this.loadAll();
      },
      error: (e) => {
        this.error.set(e.error?.error || 'Falha no acesso');
        this.busy.set(false);
      },
    });
  }
  logout() {
    localStorage.removeItem('domusflow_token');
    this.user.set(null);
    this.tasks.set([]);
  }
  loadAll() {
    this.busy.set(true);
    this.http.get<User>(`${this.api}/me`).subscribe({
      next: (u) => {
        this.user.set(u);
        Promise.all([
          this.loadTasks(),
          this.loadDashboard(),
          this.loadRewards(),
          u.role === 'OWNER' ? this.loadUsers() : Promise.resolve(),
        ]).finally(() => this.busy.set(false));
      },
      error: () => {
        this.logout();
        this.busy.set(false);
      },
    });
  }
  loadTasks() {
    return new Promise<void>((resolve) =>
      this.http.get<Task[]>(`${this.api}/tasks?date=${this.selectedDate}`).subscribe({
        next: (v) => {
          this.tasks.set(v);
          this.scheduleNotifications(v);
          resolve();
        },
        error: (e) => {
          this.error.set(e.error?.error || 'Erro ao carregar tarefas');
          resolve();
        },
      }),
    );
  }
  loadDashboard() {
    return new Promise<void>((resolve) =>
      this.http.get<any>(`${this.api}/dashboard?date=${this.selectedDate}`).subscribe({
        next: (v) => {
          this.dashboard.set(v);
          resolve();
        },
        error: () => resolve(),
      }),
    );
  }
  loadUsers() {
    return new Promise<void>((resolve) =>
      this.http.get<User[]>(`${this.api}/users`).subscribe({
        next: (v) => {
          this.users.set(v);
          if (!this.taskForm.assigneeId && v.length)
            this.taskForm.assigneeId = v.find((x) => x.role === 'EMPLOYEE')?.id || v[0].id;
          resolve();
        },
        error: () => resolve(),
      }),
    );
  }
  loadRewards() {
    return new Promise<void>((resolve) =>
      this.http.get<any[]>(`${this.api}/rewards`).subscribe({
        next: (v) => {
          this.rewards.set(v);
          resolve();
        },
        error: () => resolve(),
      }),
    );
  }
  changeDate() {
    this.taskForm.scheduledDate = this.selectedDate;
    this.loadTasks();
    this.loadDashboard();
  }
  checkIn() {
    this.action(`${this.api}/work/check-in`, {}, 'Início de trabalho validado');
  }
  checkOut() {
    this.action(`${this.api}/work/check-out`, {}, 'Jornada terminada');
  }
  start(t: Task) {
    this.action(`${this.api}/tasks/${t.id}/start`, {}, 'Tarefa iniciada');
  }
  complete(t: Task) {
    this.action(`${this.api}/tasks/${t.id}/complete`, {}, 'Tarefa concluída e XP atribuído');
  }
  action(url: string, body: any, msg: string) {
    this.error.set('');
    this.http.post(url, body).subscribe({
      next: () => {
        this.notice.set(msg);
        this.loadTasks();
        this.loadDashboard();
        this.loadRewards();
        setTimeout(() => this.notice.set(''), 2500);
      },
      error: (e: any) => this.error.set(e.error?.error || 'Operação indisponível'),
    });
  }
  createTask() {
    this.error.set('');
    this.http.post(`${this.api}/tasks`, this.taskForm).subscribe({
      next: () => {
        this.showTaskForm = false;
        this.notice.set('Tarefa criada');
        this.taskForm = { ...this.taskForm, title: '', description: '' };
        this.loadTasks();
        this.loadDashboard();
      },
      error: (e) => this.error.set(e.error?.error || 'Erro ao criar tarefa'),
    });
  }
  remove(t: Task) {
    if (!confirm(`Eliminar "${t.title}"?`)) return;
    this.http.delete(`${this.api}/tasks/${t.id}`).subscribe({
      next: () => {
        this.loadTasks();
        this.loadDashboard();
      },
      error: (e) => this.error.set(e.error?.error || 'Não foi possível eliminar'),
    });
  }

  scheduleNotifications(tasks: Task[]) {
    if (!('Notification' in window)) return;
    if (Notification.permission === 'default') Notification.requestPermission();
    if (
      Notification.permission !== 'granted' ||
      this.selectedDate !== new Date().toISOString().slice(0, 10)
    )
      return;
    for (const task of tasks.filter((t) => t.status === 'PLANNED' && t.startTime)) {
      const due = new Date(`${this.selectedDate}T${task.startTime}:00`).getTime();
      const delay = due - Date.now() - 10 * 60 * 1000;
      if (delay > 0 && delay < 86400000)
        setTimeout(
          () => new Notification('DomusFlow · tarefa em 10 minutos', { body: task.title }),
          delay,
        );
    }
  }
  progress() {
    const d = this.dashboard();
    return d.total ? Math.round((d.done / d.total) * 100) : 0;
  }
  statusLabel(s: string) {
    return (
      (
        {
          PLANNED: 'Planeada',
          IN_PROGRESS: 'Em curso',
          DONE: 'Concluída',
          SKIPPED: 'Ignorada',
        } as any
      )[s] || s
    );
  }
  roleLabel(r: string) {
    return ({ OWNER: 'Gestora', EMPLOYEE: 'Empregada', CHILD: 'Filho' } as any)[r] || r;
  }
}
