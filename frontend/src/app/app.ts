import { CommonModule } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnDestroy, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';

type UserRole = 'OWNER' | 'EMPLOYEE' | 'CHILD';
type TaskStatus = 'PLANNED' | 'IN_PROGRESS' | 'DONE' | 'SKIPPED';
type RewardType = 'BONUS' | 'DAY_OFF';

interface User {
  id: string;
  householdId?: string;
  householdName?: string;
  name: string;
  email: string;
  role: UserRole;
  avatar: string;
}

interface Task {
  id: string;
  title: string;
  description: string;
  scheduledDate: string;
  startTime?: string | null;
  estimatedMinutes: number;
  priority: number;
  status: TaskStatus;
  xpAwarded: number;
  assignee: User;
}

interface Dashboard {
  date: string;
  total: number;
  done: number;
  inProgress: number;
  dayXP: number;
  monthXP: number;
  bonusThreshold: number;
  dayOffThreshold: number;
  workLocked: boolean;
  lockReason: string;
  checkedIn: boolean;
}

interface Reward {
  id: string;
  month: string;
  type: RewardType;
  xpCost: number;
  status: 'AVAILABLE' | 'CLAIMED' | 'APPROVED' | 'REJECTED';
  createdAt: string;
}

interface TaskForm {
  title: string;
  description: string;
  scheduledDate: string;
  startTime: string;
  estimatedMinutes: number;
  priority: number;
  assigneeId: string;
}

const emptyDashboard: Dashboard = {
  date: '',
  total: 0,
  done: 0,
  inProgress: 0,
  dayXP: 0,
  monthXP: 0,
  bonusThreshold: 1000,
  dayOffThreshold: 1500,
  workLocked: false,
  lockReason: '',
  checkedIn: false,
};

@Component({
  selector: 'app-root',
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  private readonly api = '/api';
  private notificationTimers: number[] = [];
  private noticeTimer?: number;

  readonly user = signal<User | null>(null);
  readonly tasks = signal<Task[]>([]);
  readonly users = signal<User[]>([]);
  readonly dashboard = signal<Dashboard>(emptyDashboard);
  readonly rewards = signal<Reward[]>([]);
  readonly busy = signal(false);
  readonly actionBusy = signal<string | null>(null);
  readonly error = signal('');
  readonly notice = signal('');
  readonly searchTerm = signal('');
  readonly statusFilter = signal<'ALL' | TaskStatus>('ALL');

  readonly progress = computed(() => {
    const dashboard = this.dashboard();
    return dashboard.total ? Math.round((dashboard.done / dashboard.total) * 100) : 0;
  });

  readonly visibleTasks = computed(() => {
    const search = this.searchTerm().trim().toLocaleLowerCase('pt');
    const status = this.statusFilter();
    return this.tasks().filter((task) => {
      const matchesStatus = status === 'ALL' || task.status === status;
      const matchesSearch =
        !search ||
        `${task.title} ${task.description} ${task.assignee.name}`
          .toLocaleLowerCase('pt')
          .includes(search);
      return matchesStatus && matchesSearch;
    });
  });

  selectedDate = this.localDateIso();
  showTaskForm = false;
  showPassword = false;
  editingTaskId: string | null = null;
  loginData = { email: 'ana@demo.local', password: 'Demo123!' };
  taskForm: TaskForm = this.createEmptyTaskForm();

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    if (localStorage.getItem('domusflow_token')) {
      void this.loadAll();
    }
  }

  ngOnDestroy(): void {
    this.clearNotificationTimers();
    if (this.noticeTimer) window.clearTimeout(this.noticeTimer);
  }

  async login(): Promise<void> {
    if (!this.loginData.email.trim() || !this.loginData.password) {
      this.error.set('Preencha o e-mail e a palavra-passe.');
      return;
    }

    this.busy.set(true);
    this.error.set('');
    try {
      const response = await firstValueFrom(
        this.http.post<{ token: string; user: User }>(`${this.api}/auth/login`, this.loginData),
      );
      localStorage.setItem('domusflow_token', response.token);
      this.user.set(response.user);
      await this.loadAll();
    } catch (error) {
      this.error.set(this.apiError(error, 'Falha no acesso. Verifique as credenciais.'));
    } finally {
      this.busy.set(false);
    }
  }

  logout(): void {
    localStorage.removeItem('domusflow_token');
    this.user.set(null);
    this.tasks.set([]);
    this.users.set([]);
    this.rewards.set([]);
    this.dashboard.set(emptyDashboard);
    this.closeTaskForm();
    this.clearNotificationTimers();
  }

  selectDemo(email: string): void {
    this.loginData = { email, password: 'Demo123!' };
    this.error.set('');
  }

  async loadAll(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      const currentUser = await firstValueFrom(this.http.get<User>(`${this.api}/me`));
      this.user.set(currentUser);
      await Promise.all([
        this.loadTasks(),
        this.loadDashboard(),
        this.loadRewards(),
        currentUser.role === 'OWNER' ? this.loadUsers() : Promise.resolve(),
      ]);
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.logout();
      } else {
        this.error.set(this.apiError(error, 'Não foi possível carregar o painel.'));
      }
    } finally {
      this.busy.set(false);
    }
  }

  async loadTasks(): Promise<void> {
    const tasks = await firstValueFrom(
      this.http.get<Task[]>(`${this.api}/tasks?date=${this.selectedDate}`),
    );
    this.tasks.set(tasks);
    this.scheduleNotifications(tasks);
  }

  async loadDashboard(): Promise<void> {
    const dashboard = await firstValueFrom(
      this.http.get<Dashboard>(`${this.api}/dashboard?date=${this.selectedDate}`),
    );
    this.dashboard.set(dashboard);
  }

  async loadUsers(): Promise<void> {
    const users = await firstValueFrom(this.http.get<User[]>(`${this.api}/users`));
    this.users.set(users);
    if (!this.taskForm.assigneeId && users.length) {
      this.taskForm.assigneeId =
        users.find((candidate) => candidate.role === 'EMPLOYEE')?.id ?? users[0].id;
    }
  }

  async loadRewards(): Promise<void> {
    this.rewards.set(await firstValueFrom(this.http.get<Reward[]>(`${this.api}/rewards`)));
  }

  async changeDate(): Promise<void> {
    this.error.set('');
    this.taskForm.scheduledDate = this.selectedDate;
    this.busy.set(true);
    try {
      await Promise.all([this.loadTasks(), this.loadDashboard()]);
    } catch (error) {
      this.error.set(this.apiError(error, 'Erro ao alterar a data.'));
    } finally {
      this.busy.set(false);
    }
  }

  openCreateTask(): void {
    this.editingTaskId = null;
    this.taskForm = this.createEmptyTaskForm();
    const employee = this.users().find((candidate) => candidate.role === 'EMPLOYEE');
    this.taskForm.assigneeId = employee?.id ?? this.users()[0]?.id ?? '';
    this.showTaskForm = true;
  }

  editTask(task: Task): void {
    this.editingTaskId = task.id;
    this.taskForm = {
      title: task.title,
      description: task.description,
      scheduledDate: task.scheduledDate,
      startTime: task.startTime ?? '',
      estimatedMinutes: task.estimatedMinutes,
      priority: task.priority,
      assigneeId: task.assignee.id,
    };
    this.showTaskForm = true;
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  closeTaskForm(): void {
    this.showTaskForm = false;
    this.editingTaskId = null;
    this.taskForm = this.createEmptyTaskForm();
  }

  async saveTask(): Promise<void> {
    if (!this.taskForm.title.trim() || !this.taskForm.assigneeId) {
      this.error.set('Indique o título e o responsável da tarefa.');
      return;
    }

    this.actionBusy.set('task-form');
    this.error.set('');
    try {
      if (this.editingTaskId) {
        await firstValueFrom(
          this.http.patch(`${this.api}/tasks/${this.editingTaskId}`, this.taskForm),
        );
        this.showNotice('Tarefa atualizada.');
      } else {
        await firstValueFrom(this.http.post(`${this.api}/tasks`, this.taskForm));
        this.showNotice('Tarefa criada.');
      }
      this.closeTaskForm();
      await Promise.all([this.loadTasks(), this.loadDashboard()]);
    } catch (error) {
      this.error.set(this.apiError(error, 'Não foi possível guardar a tarefa.'));
    } finally {
      this.actionBusy.set(null);
    }
  }

  checkIn(): Promise<void> {
    return this.runAction('check-in', `${this.api}/work/check-in`, 'Início de trabalho validado.');
  }

  checkOut(): Promise<void> {
    return this.runAction('check-out', `${this.api}/work/check-out`, 'Jornada terminada.');
  }

  start(task: Task): Promise<void> {
    return this.runAction(`start-${task.id}`, `${this.api}/tasks/${task.id}/start`, 'Tarefa iniciada.');
  }

  complete(task: Task): Promise<void> {
    return this.runAction(
      `complete-${task.id}`,
      `${this.api}/tasks/${task.id}/complete`,
      'Tarefa concluída e XP atribuído.',
    );
  }

  async remove(task: Task): Promise<void> {
    if (!confirm(`Eliminar “${task.title}”? Esta ação não pode ser anulada.`)) return;

    this.actionBusy.set(`delete-${task.id}`);
    this.error.set('');
    try {
      await firstValueFrom(this.http.delete(`${this.api}/tasks/${task.id}`));
      this.showNotice('Tarefa eliminada.');
      await Promise.all([this.loadTasks(), this.loadDashboard()]);
    } catch (error) {
      this.error.set(this.apiError(error, 'Não foi possível eliminar a tarefa.'));
    } finally {
      this.actionBusy.set(null);
    }
  }

  async claimReward(reward: Reward): Promise<void> {
    this.actionBusy.set(`reward-${reward.id}`);
    this.error.set('');
    try {
      await firstValueFrom(this.http.post(`${this.api}/rewards/${reward.id}/claim`, {}));
      this.showNotice('Recompensa solicitada.');
      await this.loadRewards();
    } catch (error) {
      this.error.set(this.apiError(error, 'Não foi possível solicitar a recompensa.'));
    } finally {
      this.actionBusy.set(null);
    }
  }

  async enableNotifications(): Promise<void> {
    if (!('Notification' in window)) {
      this.error.set('Este navegador não suporta notificações.');
      return;
    }

    const permission = await Notification.requestPermission();
    if (permission === 'granted') {
      this.showNotice('Lembretes ativados para as tarefas de hoje.');
      this.scheduleNotifications(this.tasks());
    } else {
      this.error.set('A permissão para notificações não foi concedida.');
    }
  }

  notificationEnabled(): boolean {
    return 'Notification' in window && Notification.permission === 'granted';
  }

  availableReward(type: RewardType): Reward | undefined {
    return this.rewards().find((reward) => reward.type === type && reward.status === 'AVAILABLE');
  }

  rewardProgress(type: RewardType): number {
    const dashboard = this.dashboard();
    const target = type === 'BONUS' ? dashboard.bonusThreshold : dashboard.dayOffThreshold;
    return target ? Math.min(100, Math.round((dashboard.monthXP / target) * 100)) : 0;
  }

  isToday(): boolean {
    return this.selectedDate === this.localDateIso();
  }

  dateLabel(): string {
    const date = new Date(`${this.selectedDate}T12:00:00`);
    return new Intl.DateTimeFormat('pt-AO', {
      weekday: 'long',
      day: '2-digit',
      month: 'long',
    }).format(date);
  }

  firstName(): string {
    return this.user()?.name.split(' ')[0] ?? '';
  }

  statusLabel(status: TaskStatus): string {
    return (
      {
        PLANNED: 'Planeada',
        IN_PROGRESS: 'Em curso',
        DONE: 'Concluída',
        SKIPPED: 'Ignorada',
      } satisfies Record<TaskStatus, string>
    )[status];
  }

  roleLabel(role: UserRole): string {
    return (
      { OWNER: 'Gestora', EMPLOYEE: 'Colaboradora', CHILD: 'Filho' } satisfies Record<
        UserRole,
        string
      >
    )[role];
  }

  priorityLabel(priority: number): string {
    return priority === 3 ? 'Alta' : priority === 2 ? 'Média' : 'Baixa';
  }

  private async runAction(key: string, url: string, message: string): Promise<void> {
    this.actionBusy.set(key);
    this.error.set('');
    try {
      await firstValueFrom(this.http.post(url, {}));
      this.showNotice(message);
      await Promise.all([this.loadTasks(), this.loadDashboard(), this.loadRewards()]);
    } catch (error) {
      this.error.set(this.apiError(error, 'Operação indisponível.'));
    } finally {
      this.actionBusy.set(null);
    }
  }

  private scheduleNotifications(tasks: Task[]): void {
    this.clearNotificationTimers();
    if (!('Notification' in window) || Notification.permission !== 'granted' || !this.isToday()) {
      return;
    }

    for (const task of tasks.filter((item) => item.status === 'PLANNED' && item.startTime)) {
      const dueAt = new Date(`${this.selectedDate}T${task.startTime}:00`).getTime();
      const delay = dueAt - Date.now() - 10 * 60 * 1000;
      if (delay > 0 && delay < 24 * 60 * 60 * 1000) {
        this.notificationTimers.push(
          window.setTimeout(
            () =>
              new Notification('DomusFlow · tarefa em 10 minutos', {
                body: task.title,
                icon: '/assets/brand/domusflow_icon.png',
              }),
            delay,
          ),
        );
      }
    }
  }

  private clearNotificationTimers(): void {
    this.notificationTimers.forEach((timer) => window.clearTimeout(timer));
    this.notificationTimers = [];
  }

  private createEmptyTaskForm(): TaskForm {
    return {
      title: '',
      description: '',
      scheduledDate: this.selectedDate,
      startTime: '08:00',
      estimatedMinutes: 30,
      priority: 2,
      assigneeId: '',
    };
  }

  private showNotice(message: string): void {
    this.notice.set(message);
    if (this.noticeTimer) window.clearTimeout(this.noticeTimer);
    this.noticeTimer = window.setTimeout(() => this.notice.set(''), 3500);
  }

  private apiError(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      return error.error?.error || fallback;
    }
    return fallback;
  }

  private localDateIso(): string {
    const date = new Date();
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }
}
