import type { LucideIcon } from 'lucide-react';

/** Blocos de conteúdo da central de ajuda (D-W12). Texto aceita **negrito**, `código` e [links](/internos). */
export type Block =
  | { kind: 'p'; text: string }
  | { kind: 'h'; text: string; id?: string }
  | { kind: 'steps'; items: string[] }
  | { kind: 'list'; items: string[] }
  | { kind: 'callout'; tone: 'info' | 'warning' | 'success'; text: string }
  | { kind: 'image'; src: string; alt: string; caption?: string }
  | { kind: 'code'; title?: string; tabs: { label: string; lang: string; code: string }[] }
  | { kind: 'table'; head: string[]; rows: string[][] };

export interface Article {
  slug: string;
  title: string;
  summary: string;
  category: 'Começar' | 'Usar a API' | 'Para o fiscal' | 'Integração' | 'Cadastro e conformidade' | 'Referência';
  icon: LucideIcon;
  /** Palavras extras para a busca (sinônimos, siglas). */
  keywords?: string;
  blocks: Block[];
}
