/** Deterministic operator help. This is explicitly not a language model. */
export function rulesAdvice(snapshot, task, fallbackReason = '') {
  const query = typeof task === 'string' ? task.trim().toLowerCase() : '';
  if (!query || query.length > 2000) return { ok: false, source: 'rules', executable: false, error: 'Запрос должен содержать от 1 до 2000 символов.' };
  const production = snapshot.production || {};
  if(snapshot.cabinet && /шкаф|сборк|провод|клемм|монтаж|предохран/.test(query))return {ok:true,source:'rules',executable:false,title:'Учебный шкаф',summary:snapshot.cabinet.mode==='Экзамен'?'Экзамен: проверьте монтаж и питание измерительным инструментом.':snapshot.cabinet.next||snapshot.cabinet.feedback||'Проверьте состояние учебного стенда.',notes:['Сведения переданы VR-клиентом; реальный шкаф не измерялся.']};
  const notes = ['Локальные правила по телеметрии; языковая модель не использована. Помощник не отправляет команды управления.'];
  let title = 'Состояние рабочего поста';
  let summary = `${snapshot.name || snapshot.robotId || 'Робот'}: ${snapshot.mode === 'simulation' ? 'симуляция' : 'внешний контроллер'}. `;
  summary += !snapshot.connected ? 'Нет свежего подключения. '
    : snapshot.motionKnown === false || snapshot.mode !== 'simulation' ? 'Фактическое движение по этим данным не подтверждено. '
      : snapshot.moving ? 'Симулятор движется. ' : 'Симулятор неподвижен. ';
  if (snapshot.fault) summary += `Остановка: ${snapshot.fault}. `;
  if (/конвей|лини|auto|авто|производ|выпуск|цикл/.test(query)) {
    title = 'Производственная линия';
    summary = `Режим: ${production.running ? 'AUTO' : 'остановлена'}, этап: ${production.phase || 'недоступен'}. ` +
      `Роботизированных постов: ${production.stations?.length || 0}; вышло готовых панелей: ${production.completed || 0}; операций постов: ${production.cycles || 0}; деталей на линии: ${production.parts?.length || 0}. `;
    if (production.stopReason) summary += `Причина остановки: ${production.stopReason}. `;
    else if (!production.running) summary += 'Причина остановки не указана. ';
    if (production.running) notes.push('Перед ручным управлением выберите шкаф: сервер остановит AUTO и конвейер, затем выдаст ручной пост.');
    else notes.push('Для запуска: освободите ручное управление обоими постами; в блоке «AUTO / КОНВЕЙЕР · СИМУЛЯЦИЯ» нажмите «Сброс», затем «Пуск». Окно оператора должно оставаться подключённым.');
    if (production.available === false) notes.push('AUTO недоступен в текущей конфигурации; он рассчитан только на два учебных симулятора.');
  } else if (/почему|ошиб|остан|не движ|сброс|авари/.test(query)) {
    title = 'Причина остановки';
    summary = snapshot.fault || production.stopReason || 'В телеметрии нет активной ошибки. Проверьте выбранный пост, право управления и удержание разрешающего органа.';
    notes.push('После остановки отпустите разрешающий орган, получите выбранный пост и нажмите «Сброс». Сам сброс не запускает движение.');
    notes.push('Если потеряна связь, сначала восстановите свежую телеметрию. Остановка приложения не подтверждает физическую остановку оборудования.');
  } else if (/управ|ручн|курок|контроллер|робот|рук/.test(query)) {
    title = 'Ручное ведение робота';
    notes.push('Выберите шкаф нужного поста, дождитесь права управления и свежей телеметрии, при остановке выполните «Сброс».');
    notes.push('Отпустите правый курок, затем удерживайте его и перемещайте контроллер. Отпускание прекращает ведение. На ПК для проверки: Space + W/A/S/D/Q/E.');
    if (snapshot.leaseOwner) notes.push(`Текущий владелец выбранного поста: ${snapshot.leaseOwner}.`);
  } else if (/камер|видео|изображ/.test(query)) {
    title = 'Проверка видеоканала';
    summary = 'Сервер роботов не получает состояние видеомодуля Unity. По этой телеметрии проверить камеру невозможно.';
    notes.push('В панели видео проверьте выбранный источник, статус подключения и возраст последнего кадра. Виртуальная камера показывает сцену симуляции.');
  } else {
    notes.push('Поддерживаемые вопросы: «Состояние линии», «Почему робот остановлен?», «Как включить AUTO?», «Как вести руку вручную?», «Как проверить видео?».');
  }
  return { ok: true, source: 'rules', executable: false, title, summary, notes, ...(fallbackReason ? { fallbackReason } : {}) };
}
