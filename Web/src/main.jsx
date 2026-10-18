import '@douyinfe/semi-ui/react19-adapter';
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router';
import Home from './Views/Home'

import './Common/Style.css';

createRoot(document.getElementById('root')).render(<BrowserRouter><Home /></BrowserRouter>)