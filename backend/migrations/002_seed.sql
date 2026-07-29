INSERT INTO households (id,name,country_code,timezone) VALUES ('11111111-1111-1111-1111-111111111111','Casa da Família Manuel','AO','Africa/Luanda') ON CONFLICT DO NOTHING;
INSERT INTO users(id,household_id,name,email,password_hash,role,avatar) VALUES
('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','11111111-1111-1111-1111-111111111111','Ana Manuel','ana@demo.local',crypt('Demo123!',gen_salt('bf')),'OWNER','AM'),
('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','11111111-1111-1111-1111-111111111111','Rosa Pedro','rosa@demo.local',crypt('Demo123!',gen_salt('bf')),'EMPLOYEE','RP'),
('cccccccc-cccc-cccc-cccc-cccccccccccc','11111111-1111-1111-1111-111111111111','Mateus Manuel','mateus@demo.local',crypt('Demo123!',gen_salt('bf')),'CHILD','MM') ON CONFLICT DO NOTHING;
INSERT INTO holidays(household_id,holiday_date,name,country_code) VALUES
('11111111-1111-1111-1111-111111111111','2026-01-01','Ano Novo','AO'),
('11111111-1111-1111-1111-111111111111','2026-02-04','Início da Luta Armada','AO'),
('11111111-1111-1111-1111-111111111111','2026-09-17','Dia do Herói Nacional','AO'),
('11111111-1111-1111-1111-111111111111','2026-11-11','Dia da Independência','AO'),
('11111111-1111-1111-1111-111111111111','2026-12-25','Natal','AO') ON CONFLICT DO NOTHING;
INSERT INTO tasks(household_id,assignee_id,created_by,title,description,scheduled_date,start_time,estimated_minutes,priority) VALUES
('11111111-1111-1111-1111-111111111111','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','Preparar o pequeno-almoço','Organizar a mesa e preparar o pequeno-almoço da família',CURRENT_DATE,'07:30',40,3),
('11111111-1111-1111-1111-111111111111','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','Limpar a sala','Varrer, limpar superfícies e organizar almofadas',CURRENT_DATE,'09:00',60,2),
('11111111-1111-1111-1111-111111111111','cccccccc-cccc-cccc-cccc-cccccccccccc','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','Arrumar o quarto','Guardar roupas e organizar a secretária',CURRENT_DATE,'17:00',25,2) ON CONFLICT DO NOTHING;
