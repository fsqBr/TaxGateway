-- V0014: limites dos planos de entrada compatíveis com o uso pelo portal (D-C01). O portal abre várias chamadas em
-- paralelo por tela (perfil, consumo, listas); com rajada 5 e 2 simultâneas o Sandbox recebia 429 navegando.
UPDATE plans SET rpm = 120, burst = 30, concurrency = 6 WHERE code = 'SANDBOX';
UPDATE plans SET burst = 40, concurrency = 10 WHERE code = 'ESSENCIAL';
