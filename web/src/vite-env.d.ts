/// <reference types="vite/client" />
interface ImportMetaEnv {
  /** public = landing + portal; backoffice = só backoffice; all = tudo (dev/teste). D-W03. */
  readonly VITE_APP_TARGET: 'public' | 'backoffice' | 'all';
}

/** Injetados no build (vite.config.ts, D-W11). */
declare const __APP_VERSION__: string;
declare const __APP_COMMIT__: string;
declare const __APP_BUILT_AT__: string;
declare const __CHANGELOG__: { version: string; date: string; sections: { title: string; items: string[] }[] }[];
