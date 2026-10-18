import { defineConfig } from 'vite';
import plugin from '@vitejs/plugin-react';

// https://vitejs.dev/config/
export default defineConfig({
    plugins: [plugin()],
    server: {
        port: 3000,
    },
    build: {
        emptyOutDir: true,
        outDir: '..\\Releases\\wwwroot\\',
        rollupOptions: {
            output: {
                // 将第三方库拆为独立 chunk：本地并行加载 + 业务代码更新时命中缓存
                codeSplitting: {
                    groups: [
                        { name: 'semi', test: /node_modules[\\/]@douyinfe[\\/]/ },
                        { name: 'react', test: /node_modules[\\/](react|react-dom|react-router|scheduler)[\\/]/ },
                    ],
                },
            },
        },
    }
})